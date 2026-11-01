import { basicSetup } from "codemirror";
import { python } from "@codemirror/lang-python";
import { Annotation, Compartment, EditorState } from "@codemirror/state";
import { EditorView, keymap } from "@codemirror/view";
import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import { indentWithTab } from "@codemirror/commands";
import { tags } from "@lezer/highlight";

interface DotNetReference {
    invokeMethodAsync(method: string): Promise<void>;
    dispose(): void;
}

const external = Annotation.define<boolean>();
const editable = new Compartment();
const views = new WeakMap<HTMLElement, EditorView>();

const theme = EditorView.theme(
    {
        "&": { color: "var(--light)", backgroundColor: "var(--deep)", height: "100%", fontSize: "13px" },
        ".cm-scroller": { fontFamily: "var(--mono)", lineHeight: "1.6" },
        ".cm-content": { caretColor: "var(--glow)" },
        ".cm-cursor": { borderLeftColor: "var(--glow)" },
        "&.cm-focused": { outline: "1px solid var(--klein)" },
        ".cm-gutters": { backgroundColor: "var(--deep)", color: "var(--mist)", border: "none", borderRight: "1px solid var(--rule)" },
        ".cm-activeLine": { backgroundColor: "color-mix(in srgb, var(--klein) 10%, transparent)" },
        ".cm-activeLineGutter": { backgroundColor: "color-mix(in srgb, var(--klein) 10%, transparent)", color: "var(--light)" },
        "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": {
            backgroundColor: "color-mix(in srgb, var(--klein) 45%, transparent)",
        },
    },
    { dark: true },
);

const highlight = HighlightStyle.define([
    { tag: [tags.keyword, tags.operatorKeyword, tags.controlKeyword], color: "var(--glow)" },
    { tag: [tags.string, tags.special(tags.string)], color: "var(--ochre)" },
    { tag: [tags.number, tags.bool, tags.null], color: "var(--vermilion)" },
    { tag: [tags.comment, tags.lineComment], color: "var(--mist)", fontStyle: "italic" },
    { tag: [tags.function(tags.variableName), tags.definition(tags.variableName)], color: "var(--light)", fontWeight: "600" },
    { tag: tags.meta, color: "var(--glow)" },
]);

export function create(element: HTMLElement, dotnet: DotNetReference, content: string, readOnly = false): void {
    views.get(element)?.destroy();
    const view = new EditorView({
        parent: element,
        state: EditorState.create({
            doc: content,
            extensions: [
                basicSetup,
                keymap.of([indentWithTab]),
                python(),
                theme,
                syntaxHighlighting(highlight),
                editable.of(EditorView.editable.of(!readOnly)),
                EditorView.updateListener.of(update => {
                    if (update.docChanged && !update.transactions.some(transaction => transaction.annotation(external)))
                        void dotnet.invokeMethodAsync("EditorChanged");
                }),
            ],
        }),
    });
    views.set(element, view);
}

export function getValue(element: HTMLElement): string {
    return views.get(element)?.state.doc.toString() ?? "";
}

export function setValue(element: HTMLElement, content: string): void {
    const view = views.get(element);
    if (!view) return;
    view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: content },
        annotations: external.of(true),
    });
}

export function setReadOnly(element: HTMLElement, readOnly: boolean): void {
    views.get(element)?.dispatch({ effects: editable.reconfigure(EditorView.editable.of(!readOnly)) });
}

export function dispose(element: HTMLElement): void {
    views.get(element)?.destroy();
    views.delete(element);
}

export async function download(fileName: string, stream: { arrayBuffer(): Promise<ArrayBuffer> }): Promise<void> {
    const url = URL.createObjectURL(new Blob([await stream.arrayBuffer()]));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}
