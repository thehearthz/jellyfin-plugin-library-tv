const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { JSDOM } = require("jsdom");
const fixture = require("./fixture.cjs");
const html = fs.readFileSync(
  path.join(__dirname, "../../src/LibraryTV/Web/config.html"),
  "utf8",
);
async function ready(options = {}) {
  const dom = new JSDOM(html, {
    runScripts: "dangerously",
    url: "http://localhost/",
    beforeParse: (window) => fixture.install(window, options),
  });
  await settle();
  return dom;
}
async function settle() {
  for (let i = 0; i < 8; i++) await new Promise((r) => setImmediate(r));
}
function click(window, label, container = window.document) {
  const found = [...container.querySelectorAll("button")].find(
    (x) => x.textContent.trim() === label,
  );
  assert.ok(found, "Button " + label);
  found.click();
  return found;
}
function change(window, input, value) {
  input.value = value;
  input.dispatchEvent(new window.Event("change", { bubbles: true }));
}

test("browse shows and movies, keep both selections, save existing stable channel ID", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Choose shows & movies");
    await settle();
    assert.equal(w.document.querySelectorAll(".ltv-media").length, 2);
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Movies", w.document.querySelector("[data-ui=picker-tabs]"));
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    assert.match(
      w.document.querySelector("[data-ui=picker-count]").textContent,
      /2 selected/,
    );
    click(w, "Done");
    assert.match(w.document.querySelector("main").textContent, /Night Watch/);
    assert.match(
      w.document.querySelector("main").textContent,
      /The Long Weekend/,
    );
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].Id, "evening-tv");
    assert.deepEqual(w.testApi.saved.Channels[0].Source.ItemIds, [
      fixture.content.shows[0].Id,
      fixture.content.movies[0].Id,
    ]);
    assert.equal(w.testApi.saved.StreamToken, w.testApi.config.StreamToken);
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Channels saved/,
    );
  } finally {
    dom.window.close();
  }
});

test("schedule builder persists selected shows, time, days, and episode limit", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Schedule");
    click(w, "+ Add time block");
    const inputs = w.document.querySelectorAll(".ltv-block input[type=time]");
    change(w, inputs[0], "19:30");
    change(w, inputs[1], "22:00");
    click(w, "Choose shows & movies");
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Done");
    click(w, "Save channels");
    await settle();
    const b = w.testApi.saved.Channels[0].Blocks[0];
    assert.equal(b.Start, "19:30");
    assert.equal(b.End, "22:00");
    assert.equal(b.MaxItemsPerDay, 3);
    assert.equal(b.Days.length, 7);
    assert.deepEqual(b.Source.ItemIds, [fixture.content.shows[0].Id]);
  } finally {
    dom.window.close();
  }
});

test("commercial picker stores folder selectors and clip count", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Commercials");
    change(w, w.document.querySelector("main input[type=number]"), "2");
    click(w, "Choose commercial folders or clips");
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Done");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].CommercialCount, 2);
    assert.deepEqual(w.testApi.saved.Channels[0].Commercials.FolderIds, [
      fixture.content.folders[0].Id,
    ]);
  } finally {
    dom.window.close();
  }
});

test("whole library selections use library IDs, not item IDs", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Choose shows & movies");
    click(w, "Whole libraries");
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Done");
    click(w, "Save channels");
    await settle();
    assert.equal(
      w.testApi.saved.Channels[0].Source.LibraryIds[0],
      "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    );
    assert.equal(w.testApi.saved.Channels[0].Source.ItemIds.length, 0);
  } finally {
    dom.window.close();
  }
});

test("non-administrators never request configuration or media", async () => {
  const dom = await ready({ admin: false });
  try {
    assert.equal(dom.window.testApi.calls.length, 0);
    assert.match(
      dom.window.document.querySelector("[data-ui=status]").textContent,
      /Only Jellyfin administrators/,
    );
    assert.equal(
      dom.window.document.querySelector("[data-ui=body]").hidden,
      true,
    );
  } finally {
    dom.window.close();
  }
});

test("save failure preserves unsaved selections and shows a useful error", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "+ New channel");
    w.testApi.failSave = true;
    click(w, "Save channels");
    await settle();
    assert.match(
      w.document.querySelector("[data-ui=dirty]").textContent,
      /Unsaved/,
    );
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Save rejected/,
    );
    assert.equal(w.document.querySelectorAll(".ltv-channel").length, 2);
  } finally {
    dom.window.close();
  }
});

test("existing selections resolve to names and can be removed without editing JSON", async () => {
  const config = fixture.configuration();
  config.Channels[0].Source.ItemIds = [fixture.content.shows[0].Id];
  const dom = await ready({ config });
  try {
    const w = dom.window;
    assert.match(w.document.querySelector("main").textContent, /Night Watch/);
    w.document.querySelector('button[aria-label="Remove Night Watch"]').click();
    click(w, "Save channels");
    await settle();
    assert.deepEqual(w.testApi.saved.Channels[0].Source.ItemIds, []);
  } finally {
    dom.window.close();
  }
});

test("edits made while a save is in flight remain marked unsaved", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    let release;
    w.testApi.holdSave = new Promise((resolve) => {
      release = resolve;
    });
    click(w, "+ New channel");
    click(w, "Save channels");
    change(
      w,
      w.document.querySelector("main input[type=text]"),
      "Edited while saving",
    );
    release();
    await settle();
    assert.match(
      w.document.querySelector("[data-ui=dirty]").textContent,
      /Unsaved/,
    );
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /save again/,
    );
    assert.equal(w.testApi.saved.Channels[1].Name, "New channel");
  } finally {
    dom.window.close();
  }
});

test("invalid server numbers show a readable error before saving", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    change(w, w.document.querySelector('[data-setting="GuideDays"]'), "0");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved, null);
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Days in guide must be/,
    );
  } finally {
    dom.window.close();
  }
});

test("media names are displayed as text instead of HTML", async () => {
  const original = fixture.content.shows[0].Name;
  fixture.content.shows[0].Name = "<img src=x onerror=alert(1)>";
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Choose shows & movies");
    await settle();
    assert.equal(w.document.querySelector("[data-ui=results] img"), null);
    assert.match(
      w.document.querySelector("[data-ui=results]").textContent,
      /<img src=x/,
    );
  } finally {
    fixture.content.shows[0].Name = original;
    dom.window.close();
  }
});
