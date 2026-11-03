import asyncio
import json
import os
import re
import sys
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import httpx2
from fastmcp import FastMCP
from fastmcp.prompts import Prompt
from fastmcp.prompts.base import PromptArgument
from fastmcp.resources import TextResource
from fastmcp.server.providers import FileSystemProvider
from fastmcp.server.providers.openapi import OpenAPIProvider
from starlette.requests import Request
from starlette.responses import PlainTextResponse

root = Path(__file__).resolve().parent
placeholder = re.compile(r"\{([A-Za-z_]\w*)\}")


class TemplatePrompt(Prompt):
    template: str

    async def render(self, arguments=None):
        values = arguments or {}
        return placeholder.sub(lambda match: str(values.get(match.group(1), "")), self.template)


def read_json(name):
    path = root / name
    if not path.is_file():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def fail(message):
    sys.exit(f"main.py: {message}")


def build_openapi_provider(spec):
    base_url = os.environ.get("BUILDER_BASE_URL")
    if not base_url:
        fail("openapi.json is present but BUILDER_BASE_URL is not set")
    headers = json.loads(os.environ.get("BUILDER_API_HEADERS") or "{}")
    client = httpx2.AsyncClient(base_url=base_url, headers=headers, timeout=30.0)
    return OpenAPIProvider(spec, client, validate_output=False)


def prompt_from(entry):
    template = entry["template"]
    names = list(dict.fromkeys(placeholder.findall(template)))
    return TemplatePrompt(
        name=entry["name"],
        description=entry.get("description"),
        arguments=[PromptArgument(name=name, required=True) for name in names],
        template=template,
    )


def resource_from(entry):
    return TextResource(
        uri=entry["uri"],
        name=entry["name"],
        mime_type=entry.get("mimeType", "text/plain"),
        text=entry.get("text", ""),
    )


def assert_unique_tool_names(providers):
    async def collect():
        names = []
        for provider in providers:
            names.extend(tool.name for tool in await provider.list_tools())
        return names

    with ThreadPoolExecutor(max_workers=1) as pool:
        names = pool.submit(asyncio.run, collect()).result()
    duplicates = sorted(name for name, count in Counter(names).items() if count > 1)
    if duplicates:
        fail("tool name defined more than once: " + ", ".join(duplicates))


def build_server():
    providers = []
    if (root / "mcp").is_dir():
        files = FileSystemProvider(root / "mcp")
        if files.failed_files:
            fail("tool files could not be loaded: " + "; ".join(f"{path.name}: {error}" for path, error in sorted(files.failed_files.items())))
        providers.append(files)
    spec = read_json("openapi.json")
    if spec is not None:
        providers.append(build_openapi_provider(spec))
    assert_unique_tool_names(providers)

    server = FastMCP(os.environ.get("BUILDER_SERVER_NAME", "server"), providers=providers)

    @server.custom_route("/healthz", methods=["GET"])
    async def healthz(request: Request) -> PlainTextResponse:
        return PlainTextResponse("ok")

    declared = read_json("builder.json") or {}
    for entry in declared.get("resources", []):
        server.add_resource(resource_from(entry))
    for entry in declared.get("prompts", []):
        server.add_prompt(prompt_from(entry))
    return server


mcp = build_server()
