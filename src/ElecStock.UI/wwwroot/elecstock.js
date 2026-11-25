// Small helpers for the Blazor UI (web and MAUI).

export function box(element) {
    const r = element.getBoundingClientRect();
    return { left: r.left, top: r.top, width: r.width, height: r.height };
}

// Forwards pasted images (Ctrl+V of a screenshot) to .NET while the listener is alive.
export function listenPaste(target) {
    const handler = async (e) => {
        const items = e.clipboardData ? Array.from(e.clipboardData.items) : [];
        const image = items.find(i => i.kind === "file" && i.type.startsWith("image/"));
        if (!image) return;
        const tag = document.activeElement ? document.activeElement.tagName : "";
        if (tag === "TEXTAREA") return; // pasting into a Markdown editor stays text
        e.preventDefault();
        const blob = image.getAsFile();
        const ext = (blob.type.split("/")[1] || "png").replace("jpeg", "jpg");
        const stamp = new Date().toISOString().replace(/[-:]/g, "").replace("T", "-").slice(0, 15);
        await target.invokeMethodAsync("OnPasteImage", DotNet.createJSStreamReference(blob), `paste-${stamp}.${ext}`);
    };
    document.addEventListener("paste", handler);
    return { dispose: () => document.removeEventListener("paste", handler) };
}

const themeKey = "elecstock.theme";

export function getTheme() {
    try { return localStorage.getItem(themeKey) || "auto"; } catch { return "auto"; }
}

export function setTheme(theme) {
    try { localStorage.setItem(themeKey, theme); } catch { }
    applyTheme(theme);
}

export function applyTheme(theme) {
    if (theme === "light" || theme === "dark") document.documentElement.setAttribute("data-theme", theme);
    else document.documentElement.removeAttribute("data-theme");
}

export function getSetting(key) {
    try { return localStorage.getItem("elecstock." + key); } catch { return null; }
}

export function setSetting(key, value) {
    try { localStorage.setItem("elecstock." + key, value); } catch { }
}

export async function copy(text) {
    try { await navigator.clipboard.writeText(text); } catch { }
}
