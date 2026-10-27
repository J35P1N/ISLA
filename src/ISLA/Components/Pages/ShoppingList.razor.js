const key = "isla.shopping-mode";

export function readMode() {
    try {
        const saved = JSON.parse(localStorage.getItem(key) ?? "null");
        return saved?.enabled === true && /^\d{4}-\d{2}-\d{2}$/.test(saved.week ?? "") ? saved.week : null;
    } catch { return null; }
}

export function saveMode(enabled, week) {
    try {
        if (enabled) localStorage.setItem(key, JSON.stringify({ enabled: true, week }));
        else localStorage.removeItem(key);
        return true;
    } catch { return false; }
}

const draftKey = week => `isla.shopping-price-drafts:${week}`;

export function readPriceDrafts(week) {
    try {
        const drafts = JSON.parse(localStorage.getItem(draftKey(week)) ?? "[]");
        return Array.isArray(drafts) ? drafts.filter(x =>
            typeof x?.name === "string" && typeof x?.unit === "string" && typeof x?.value === "string") : [];
    } catch { return []; }
}

function rememberPrice(event) {
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || !input.matches("[data-shopping-price]")) return;
    const { week, goodName: name, unit } = input.dataset;
    try {
        const drafts = readPriceDrafts(week).filter(x => x.name !== name || x.unit !== unit);
        drafts.push({ name, unit, value: input.value });
        localStorage.setItem(draftKey(week), JSON.stringify(drafts));
    } catch { /* Stored purchase prices remain available if local storage is blocked. */ }
}

export function watchPriceInputs() { document.addEventListener("input", rememberPrice); }
export function stopWatchingPriceInputs() { document.removeEventListener("input", rememberPrice); }

export function clearPriceDraft(week, name, unit) {
    try {
        const drafts = readPriceDrafts(week).filter(x => x.name !== name || x.unit !== unit);
        localStorage.setItem(draftKey(week), JSON.stringify(drafts));
    } catch { }
}
