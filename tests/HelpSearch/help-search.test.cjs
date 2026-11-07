const assert = require("node:assert/strict");
const { test } = require("node:test");
const { tokenize, search, excerpt } = require("../../src/FileSearch.Gui/Help/js/help-search.js");

const documents = [
    { page: "indexing.html", title: "Indexed Search", heading: "Image OCR", anchor: "ocr", text: "Recognize text in scanned PDF pages and images." },
    { page: "results.html", title: "Results", heading: "Copy path", anchor: "copy", text: "Copy the full file path to the clipboard." },
    { page: "quick-search.html", title: "Quick Search", heading: "Keyboard shortcuts", anchor: "keys", text: "Open the quick window with your global hotkey." },
];

test("matches multiple keywords together, regardless of case", () => {
    assert.equal(search(documents, "SCANNED pdf")[0].anchor, "ocr");
    assert.equal(search(documents, "scanned clipboard").length, 0);
});

test("quoted phrases must stay together and duplicate terms are ignored", () => {
    assert.deepEqual(tokenize('"copy path" COPY copy'), ["copy path", "copy"]);
    assert.equal(search(documents, '"copy path"')[0].page, "results.html");
    assert.equal(search(documents, '"scanned images"').length, 0);
});

test("heading matches outrank incidental text matches", () => {
    const incidental = { ...documents[0], heading: "General", text: "You can copy path text." };
    assert.equal(search([incidental, documents[1]], "copy path")[0].page, "results.html");
});

test("a matching topic overview precedes references in other topics", () => {
    const overview = { ...documents[0], title: "PowerToys Integration", heading: "Overview", text: "Search using PowerToys Command Palette." };
    const reference = { ...documents[1], heading: "Optional PowerToys companion", text: "PowerToys is an optional download." };
    assert.equal(search([reference, overview], "PowerToys")[0].page, "indexing.html");
});

test("broad searches retain results from different topics", () => {
    const many = Array.from({ length: 20 }, (_, index) => ({ ...documents[0], heading: `OCR ${index}` }));
    const other = { ...documents[1], text: "OCR text can be copied." };
    const results = search([...many, other], "OCR");
    assert.equal(results.length, 21);
    assert.equal(results[3].page, "results.html");
    assert.equal(search([...many, other], "OCR", 4).length, 4);
});

test("empty and missing queries produce no results", () => {
    assert.deepEqual(search(documents, "  "), []);
    assert.deepEqual(search(documents, "zebra-not-present"), []);
});

test("snippets show matching text near the middle of a long section", () => {
    const text = "intro ".repeat(60) + "needle appears here " + "later ".repeat(60);
    const snippet = excerpt(text, ["needle"]);
    assert.ok(snippet.includes("needle appears here"));
    assert.ok(snippet.startsWith("…"));
    assert.ok(snippet.endsWith("…"));
    assert.ok(snippet.length <= 202);
});
