import json
import os
import subprocess
from pathlib import Path

config_path = Path(os.environ.get("BUILDER_CONFIG", "/code/fastmcp.json"))
venv = Path(os.environ.get("BUILDER_VENV", "/env/.venv"))
lock = venv.parent / "requirements.lock"

environment = json.loads(config_path.read_text(encoding="utf-8")).get("environment") or {}
dependencies = environment.get("dependencies") or []
python_version = environment.get("python")

venv_command = ["uv", "venv", str(venv)]
if python_version:
    venv_command += ["--python", str(python_version)]
subprocess.run(venv_command, check=True)

interpreter = str(venv / "bin" / "python")
if dependencies:
    subprocess.run(["uv", "pip", "install", "--python", interpreter, *dependencies], check=True)

frozen = subprocess.run(
    ["uv", "pip", "freeze", "--python", interpreter],
    check=True,
    capture_output=True,
    text=True,
).stdout
lock.write_text(frozen, encoding="utf-8")
