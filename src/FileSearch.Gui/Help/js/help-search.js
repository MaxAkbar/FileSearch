(function (root) {
    "use strict";

    function tokenize(query) {
        return [...new Set((query.match(/"[^"]+"|[^\s"]+/g) || [])
            .map(term => term.replace(/^"|"$/g, "").toLowerCase().trim())
            .filter(Boolean))];
    }

    function search(documents, query, limit = 24) {
        const terms = tokenize(query);
        if (!terms.length) return [];
        const ranked = documents.map((document, order) => {
            const title = document.title.toLowerCase();
            const heading = document.heading.toLowerCase();
            const text = document.text.toLowerCase();
            const all = `${title} ${heading} ${text}`;
            if (!terms.every(term => all.includes(term))) return null;
            let score = 0;
            for (const term of terms) {
                score += title.includes(term) ? 30 : 0;
                if (document.heading === "Overview" && title.includes(term)) score += 30;
                score += heading.includes(term) ? 50 : 0;
                score += text.includes(term) ? 5 : 0;
            }
            const phrase = terms.join(" ");
            if (title === phrase) score += 100;
            if (heading === phrase) score += 150;
            if (text.includes(phrase)) score += 10;
            return { ...document, score, order };
        }).filter(Boolean).sort((a, b) => b.score - a.score || a.order - b.order);
        const perPage = new Map();
        const primary = [], additional = [];
        for (const result of ranked) {
            const count = perPage.get(result.page) || 0;
            perPage.set(result.page, count + 1);
            (count < 3 ? primary : additional).push(result);
        }
        return primary.concat(additional).slice(0, limit);
    }

    function excerpt(text, terms, length = 200) {
        const positions = terms.map(term => text.toLowerCase().indexOf(term)).filter(position => position >= 0);
        const first = positions.length ? Math.min(...positions) : 0;
        let start = Math.max(0, first - 65);
        if (start > 0) {
            const boundary = text.indexOf(" ", start);
            if (boundary >= 0 && boundary < first) start = boundary + 1;
        }
        const end = Math.min(text.length, start + length);
        return (start ? "…" : "") + text.slice(start, end).trim() + (end < text.length ? "…" : "");
    }

    if (typeof module !== "undefined" && module.exports) module.exports = { tokenize, search, excerpt };
    if (typeof document === "undefined") return;

    function element(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function highlight(node, text, terms) {
        if (!terms.length) { node.append(document.createTextNode(text)); return; }
        const escaped = terms.slice().sort((a, b) => b.length - a.length)
            .map(term => term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
        const expression = new RegExp(escaped.join("|"), "giu");
        let offset = 0;
        for (const match of text.matchAll(expression)) {
            node.append(document.createTextNode(text.slice(offset, match.index)));
            node.append(element("mark", "help-match", match[0]));
            offset = match.index + match[0].length;
        }
        node.append(document.createTextNode(text.slice(offset)));
    }

    function initialize() {
        const sidebar = document.querySelector(".sidebar");
        const main = document.querySelector("main.content");
        if (!sidebar || !main) return;

        // Match the build-generated anchors while preserving existing deep links.
        main.querySelectorAll("h2, h3").forEach((heading, index) => {
            if (!heading.id) heading.id = `help-section-${index}`;
        });

        const parameters = new URLSearchParams(location.search);
        const isSearchPage = main.hasAttribute("data-help-search-page");
        const query = (parameters.get(isSearchPage ? "q" : "highlight") || "").slice(0, 200);
        const form = element("form", "help-search-form");
        form.action = "help-search.html";
        form.method = "get";
        form.setAttribute("role", "search");
        form.setAttribute("aria-label", "Search FileSearch Help");
        const label = element("label", "help-search-label", "Search Help");
        label.htmlFor = "help-search-input";
        const controls = element("div", "help-search-controls");
        const input = element("input");
        input.type = "search";
        input.id = "help-search-input";
        input.name = "q";
        input.placeholder = "Find a topic or keyword";
        input.maxLength = 200;
        input.autocomplete = "off";
        input.value = query;
        const submit = element("button", "help-search-submit", "Search");
        submit.type = "submit";
        controls.append(input, submit);
        const hint = element("p", "help-search-hint", "Ctrl+K to search all topics");
        form.append(label, controls, hint);
        sidebar.querySelector(".brand").after(form);

        document.addEventListener("keydown", event => {
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
                event.preventDefault(); input.focus(); input.select();
            }
        });

        if (isSearchPage) {
            const results = document.getElementById("help-search-results");
            const status = document.getElementById("help-search-status");
            input.setAttribute("aria-controls", "help-search-results");
            input.setAttribute("aria-describedby", "help-search-status");
            let timer;
            function showResults(submitted = false) {
                if (!input.value.trim()) return;
                const narrow = window.matchMedia("(max-width: 860px)").matches;
                if (narrow) {
                    status.tabIndex = -1;
                    status.focus({ preventScroll: true });
                }
                if (narrow || submitted) status.scrollIntoView({ block: "start", behavior: "instant" });
            }
            function render(limit = 24) {
                clearTimeout(timer);
                results.replaceChildren();
                const value = input.value.trim();
                const url = new URL(location.href);
                if (value) url.searchParams.set("q", value); else url.searchParams.delete("q");
                history.replaceState(null, "", url);
                if (!value) { status.textContent = "Enter a word or phrase to search all Help topics."; return; }
                if (!Array.isArray(root.FileSearchHelpIndex)) {
                    status.textContent = "Help search is unavailable. You can still browse topics from the sidebar.";
                    return;
                }
                const matches = search(root.FileSearchHelpIndex, value, root.FileSearchHelpIndex.length);
                status.textContent = matches.length
                    ? `${matches.length > limit ? `Showing ${limit} of ${matches.length}` : matches.length} matching ${matches.length === 1 ? "section" : "sections"} for “${value}”.`
                    : `No matches for “${value}”. Try a shorter phrase or another keyword.`;
                const terms = tokenize(value);
                for (const match of matches.slice(0, limit)) {
                    const item = element("li", "help-search-result");
                    const link = element("a", "help-search-result-link");
                    link.href = `${match.page}?highlight=${encodeURIComponent(value)}${match.anchor ? "#" + encodeURIComponent(match.anchor) : ""}`;
                    highlight(link, match.heading === "Overview" ? match.title : match.heading, terms);
                    const topic = element("p", "help-search-topic", match.title);
                    const snippet = element("p", "help-search-snippet");
                    highlight(snippet, excerpt(match.text, terms), terms);
                    item.append(topic, link, snippet);
                    results.append(item);
                }
                if (matches.length > limit) {
                    const item = element("li");
                    const more = element("button", "help-search-submit", `Show all ${matches.length} matching sections`);
                    more.type = "button";
                    more.addEventListener("click", () => {
                        render(matches.length);
                        results.querySelectorAll("a")[limit]?.focus();
                    });
                    item.append(more);
                    results.append(item);
                }
            }
            form.addEventListener("submit", event => { event.preventDefault(); render(); showResults(true); });
            input.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(render, 120); });
            input.addEventListener("keydown", event => {
                if (event.key === "ArrowDown") {
                    const first = results.querySelector("a");
                    if (first) { event.preventDefault(); first.focus(); }
                } else if (event.key === "Escape") {
                    event.preventDefault(); input.value = ""; render();
                }
            });
            render();
            input.focus({ preventScroll: true });
            showResults();
        } else if (query) {
            const terms = tokenize(query);
            const walker = document.createTreeWalker(main, NodeFilter.SHOW_TEXT, {
                acceptNode: node => node.parentElement.closest("script, style, mark") ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
            });
            const nodes = [];
            while (walker.nextNode()) nodes.push(walker.currentNode);
            for (const text of nodes) {
                if (!terms.some(term => text.textContent.toLowerCase().includes(term))) continue;
                const fragment = document.createDocumentFragment();
                highlight(fragment, text.textContent, terms);
                text.replaceWith(fragment);
            }
            const context = element("div", "help-search-context");
            context.append(document.createTextNode(`Matches for “${query}” · `));
            const back = element("a", "", "Back to search results");
            back.href = `help-search.html?q=${encodeURIComponent(query)}`;
            context.append(back);
            main.prepend(context);
        }

        if (location.hash) {
            let id;
            try { id = decodeURIComponent(location.hash.slice(1)); } catch { return; }
            const target = document.getElementById(id);
            if (target) requestAnimationFrame(() => target.scrollIntoView({ block: "start" }));
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", initialize);
    else initialize();
})(typeof window === "undefined" ? globalThis : window);
