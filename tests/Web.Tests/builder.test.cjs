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

test("custom subchannel numbers are saved without changing the channel ID", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    change(
      w,
      w.document.querySelector('input[placeholder="e.g. 42, 7.2 or 7-10"]'),
      "7-10",
    );
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].Number, "7-10");
    assert.equal(w.testApi.saved.Channels[0].Id, "evening-tv");
  } finally {
    dom.window.close();
  }
});

test("equivalent duplicate numbers are caught before saving", async () => {
  const config = fixture.configuration();
  config.Channels[0].Number = "7.2";
  const dom = await ready({ config });
  try {
    const w = dom.window;
    click(w, "+ New channel");
    change(
      w,
      w.document.querySelector('input[placeholder="e.g. 42, 7.2 or 7-10"]'),
      "007-02",
    );
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved, null);
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /used by both/,
    );
  } finally {
    dom.window.close();
  }
});

test("new channels use a free integer instead of treating subchannels as decimals", async () => {
  const config = fixture.configuration();
  config.Channels[0].Number = "7.2";
  const dom = await ready({ config });
  try {
    const w = dom.window;
    click(w, "+ New channel");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[1].Number, "1");
  } finally {
    dom.window.close();
  }
});

test("automatic channels accept custom station names and numbers", async () => {
  const config = fixture.configuration();
  config.AutoChannels = true;
  config.AutoLibraryIds = ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"];
  const dom = await ready({ config });
  try {
    const w = dom.window;
    change(
      w,
      w.document.querySelector('input[placeholder="TV Shows"]'),
      "My station",
    );
    change(
      w,
      w.document.querySelector('input[placeholder="Assigned automatically"]'),
      "42.1",
    );
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.AutomaticChannels[0].Name, "My station");
    assert.equal(w.testApi.saved.AutomaticChannels[0].Number, "42.1");
    assert.equal(w.testApi.saved.AutoChannelStartNumber, 100);
  } finally {
    dom.window.close();
  }
});

test("sidebar sorts subchannels numerically", async () => {
  const config = fixture.configuration();
  const original = config.Channels[0];
  config.Channels = ["7-10", "7.2", "7"].map((number, i) => ({
    ...structuredClone(original),
    Id: "ch" + i,
    Number: number,
  }));
  const dom = await ready({ config });
  try {
    assert.deepEqual(
      [...dom.window.document.querySelectorAll(".ltv-channel strong")].map(
        (x) => x.textContent.split(" · ")[0],
      ),
      ["7", "7.2", "7-10"],
    );
  } finally {
    dom.window.close();
  }
});

test("channel health checks run on request without starting playback", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const before = w.testApi.calls.length;
    click(w, "Refresh status");
    await settle();
    assert.deepEqual(w.testApi.calls.slice(before), ["status"]);
    assert.match(
      w.document.querySelector("[data-ui=health]").textContent,
      /No enabled channels/,
    );
  } finally {
    dom.window.close();
  }
});

test("builder can retry after the server's startup settling period", async () => {
  const dom = new JSDOM(html, {
    runScripts: "dangerously",
    url: "http://localhost/",
    beforeParse: (window) => {
      fixture.install(window);
      window.testApi.starting = true;
    },
  });
  try {
    await settle();
    const w = dom.window;
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /waiting for Jellyfin startup/,
    );
    assert.equal(w.document.querySelector("[data-ui=body]").hidden, true);
    assert.equal(
      w.document.querySelector("[data-action=retry-load]").hidden,
      false,
    );
    w.testApi.starting = false;
    click(w, "Retry loading builder");
    await settle();
    assert.equal(w.document.querySelector("[data-ui=body]").hidden, false);
    assert.equal(
      w.document.querySelector("[data-action=retry-load]").hidden,
      true,
    );
  } finally {
    dom.window.close();
  }
});

test("saved channel guide can rebuild future slots through the explicit action", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Guide preview");
    click(w, "Rebuild future slots");
    await settle();
    assert.equal(w.testApi.rebuilt, "rebuild/evening-tv");
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Future slots rebuilt/,
    );
  } finally {
    dom.window.close();
  }
});
test("rebuilding never silently discards unsaved channel edits", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "+ New channel");
    click(w, "Guide preview");
    click(w, "Rebuild future slots");
    await settle();
    assert.equal(w.testApi.rebuilt, undefined);
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Save your changes/,
    );
  } finally {
    dom.window.close();
  }
});
test("health view explains source and runtime problems without starting streams", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    w.testApi.healthChannels = [
      {
        Name: "Test",
        Number: "42",
        AlwaysEncode: false,
        HasUpcomingPrograms: false,
        Warnings: ["No readable commercial clips were found."],
        Runtime: {
          Viewers: 0,
          Recoveries: 2,
          LastProblem: "A scheduled file is missing.",
          Playback: "Video copied; audio encoded to AAC stereo",
        },
      },
    ];
    click(w, "Refresh status");
    await settle();
    const text = w.document.querySelector("[data-ui=health]").textContent;
    assert.match(text, /commercial clips/);
    assert.match(text, /file is missing/);
    assert.match(text, /2 automatic recoveries/);
    assert.match(text, /encodes only programs whose formats need it/);
    assert.match(text, /Now: Video copied; audio encoded/);
    assert.ok(!w.testApi.calls.some((x) => x.startsWith("stream/")));
  } finally {
    dom.window.close();
  }
});
test("backup review and restore preserves connection settings and reloads cleanly", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const draft = { FormatVersion: 1, ...fixture.configuration() };
    delete draft.StreamToken;
    delete draft.InternalBaseUrl;
    draft.Channels[0].Name = "Restored station";
    const file = { size: 1000, text: async () => JSON.stringify(draft) };
    const input = w.document.querySelector("[data-ui=backup-file]");
    Object.defineProperty(input, "files", { value: [file] });
    input.dispatchEvent(new w.Event("change", { bubbles: true }));
    await settle();
    assert.match(
      w.document.querySelector("[data-ui=backup-preview]").textContent,
      /Restored station/,
    );
    click(w, "Restore reviewed settings");
    await settle();
    assert.equal(
      w.testApi.config.StreamToken,
      fixture.configuration().StreamToken,
    );
    assert.equal(
      w.document.querySelector("main input[type=text]").value,
      "Restored station",
    );
    change(w, w.document.querySelector("[data-setting=GuideDays]"), "2");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.GuideDays, 2);
  } finally {
    dom.window.close();
  }
});
test("oversized or unsupported backup files cannot be restored", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const input = w.document.querySelector("[data-ui=backup-file]");
    Object.defineProperty(input, "files", {
      value: [{ size: 1048577, text: async () => "{}" }],
    });
    input.dispatchEvent(new w.Event("change", { bubbles: true }));
    await settle();
    assert.match(
      w.document.querySelector("[data-ui=backup-preview]").textContent,
      /smaller than 1 MB/,
    );
    assert.equal(
      w.document.querySelector("[data-action=import-backup]").disabled,
      true,
    );
    assert.equal(w.testApi.imported, undefined);
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

function episodesInARow(container) {
  return [...container.querySelectorAll("label.ltv-field")]
    .filter((x) => x.querySelector("span").textContent === "Episodes in a row")
    .map((x) => x.querySelector("input"));
}

test("shows default to two episodes in a row and the setting saves per lineup and block", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const [lineup] = episodesInARow(w.document);
    assert.equal(lineup.value, "2");
    change(w, lineup, "3");
    click(w, "Schedule");
    click(w, "+ Add time block");
    const [block] = episodesInARow(w.document.querySelector(".ltv-block"));
    assert.equal(block.value, "2");
    change(w, block, "1");
    click(w, "Save channels");
    await settle();
    const c = w.testApi.saved.Channels[0];
    assert.equal(c.Source.EpisodesInARow, 3);
    assert.equal(c.Blocks[0].Source.EpisodesInARow, 1);
  } finally {
    dom.window.close();
  }
});

test("commercial breaks do not offer an episodes-in-a-row setting", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Commercials");
    assert.equal(episodesInARow(w.document).length, 0);
  } finally {
    dom.window.close();
  }
});

test("invalid episodes-in-a-row values show a readable error before saving", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    change(w, episodesInARow(w.document)[0], "-1");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved, null);
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Episodes in a row must be/,
    );
  } finally {
    dom.window.close();
  }
});

function checkbox(window, label) {
  const found = [...window.document.querySelectorAll("label.ltv-check")].find(
    (x) => x.textContent.includes(label),
  );
  assert.ok(found, "Checkbox " + label);
  return found.querySelector("input");
}

test("playback and commercial era options default on older channels and save per channel", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const encode = checkbox(w, "Always encode every program");
    assert.equal(encode.checked, false);
    encode.click();
    click(w, "Commercials");
    const era = checkbox(w, "Match commercials to when the program");
    assert.equal(era.checked, true);
    era.click();
    click(w, "Save channels");
    await settle();
    const c = w.testApi.saved.Channels[0];
    assert.equal(c.AlwaysEncode, true);
    assert.equal(c.MatchCommercialEra, false);
    assert.ok(!("Transcode" in c) || c.Transcode === undefined);
  } finally {
    dom.window.close();
  }
});

test("content restrictions are added for every channel with times, days, genres and tags", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const panel = w.document.querySelector("[data-ui=restrictions]");
    assert.match(panel.textContent, /No restrictions/);
    click(w, "+ Add restriction");
    const [from, until] = panel.querySelectorAll("input[type=time]");
    assert.equal(from.value, "06:00");
    assert.equal(until.value, "21:00");
    change(w, until, "20:30");
    const [genres, tags] = [...panel.querySelectorAll("label.ltv-field")]
      .filter((x) => /Genres|Tags/.test(x.textContent))
      .map((x) => x.querySelector("input"));
    assert.equal(genres.value, "Horror");
    change(w, genres, "Horror, Thriller");
    change(w, tags, "scary");
    [...panel.querySelectorAll(".ltv-days input")][0].click();
    click(w, "Save channels");
    await settle();
    const [rule] = w.testApi.saved.Restrictions;
    assert.equal(rule.Start, "06:00");
    assert.equal(rule.End, "20:30");
    assert.deepEqual(rule.Genres, ["Horror", "Thriller"]);
    assert.deepEqual(rule.Tags, ["scary"]);
    assert.deepEqual(rule.Days, [1, 2, 3, 4, 5, 6]);
    click(w, "Remove", panel);
    click(w, "Save channels");
    await settle();
    assert.deepEqual(w.testApi.saved.Restrictions, []);
  } finally {
    dom.window.close();
  }
});

test("a content restriction without a genre or tag shows a readable error before saving", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "+ Add restriction");
    const genres = [
      ...w.document.querySelectorAll("[data-ui=restrictions] label.ltv-field"),
    ]
      .find((x) => /Genres/.test(x.textContent))
      .querySelector("input");
    change(w, genres, " ");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved, null);
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /needs at least one genre or tag/,
    );
  } finally {
    dom.window.close();
  }
});

test("commercial networks default on and save per channel and time block", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Commercials");
    assert.equal(checkbox(w, "Match commercials to the network").checked, true);
    const field = (label) =>
      [...w.document.querySelectorAll("main label.ltv-field")]
        .find((x) => x.textContent.includes(label))
        .querySelector("input");
    change(w, field("Commercial network for this channel"), " Toonami ");
    click(w, "Schedule");
    click(w, "+ Add time block");
    change(w, field("Commercial network for this block"), "Adult Swim");
    click(w, "Save channels");
    await settle();
    const c = w.testApi.saved.Channels[0];
    assert.equal(c.MatchCommercialNetwork, true);
    assert.equal(c.CommercialNetwork, "Toonami");
    assert.equal(c.Blocks[0].CommercialNetwork, "Adult Swim");
  } finally {
    dom.window.close();
  }
});

test("bumpers have their own section and save their folder selection", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Bumpers");
    assert.match(
      w.document.querySelector("main").textContent,
      /going into each commercial break/,
    );
    click(w, "Choose bumper folders or clips");
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Done");
    click(w, "Save channels");
    await settle();
    assert.deepEqual(w.testApi.saved.Channels[0].Bumpers.FolderIds, [
      fixture.content.folders[0].Id,
    ]);
  } finally {
    dom.window.close();
  }
});

test("breaks during programs default to one per episode and two per movie and save", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Commercials");
    const field = (label) =>
      [...w.document.querySelectorAll("main label.ltv-field")]
        .find((x) => x.textContent.includes(label))
        .querySelector("input");
    assert.equal(field("Breaks during each episode").value, "1");
    assert.equal(field("Breaks during each movie").value, "2");
    change(w, field("Breaks during each movie"), "3");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].EpisodeBreaks, 1);
    assert.equal(w.testApi.saved.Channels[0].MovieBreaks, 3);
    change(w, field("Breaks during each episode"), "9");
    click(w, "Save channels");
    await settle();
    assert.match(
      w.document.querySelector("[data-ui=status]").textContent,
      /Breaks during a program must be/,
    );
  } finally {
    dom.window.close();
  }
});

test("picture shape defaults to widescreen and saves per channel", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const select = [...w.document.querySelectorAll("main label.ltv-field")]
      .find((x) => x.textContent.includes("Picture shape"))
      .querySelector("select");
    assert.equal(select.value, "16:9");
    change(w, select, "4:3");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].PictureShape, "4:3");
  } finally {
    dom.window.close();
  }
});

test("shared commercials are chosen once for every channel and show how they were sorted", async () => {
  const dom = await ready({
    clipLibrary: [
      { Kind: "Commercials", Network: "Nickelodeon", Decade: 1990, Count: 42 },
      { Kind: "Commercials", Network: "Nickelodeon", Decade: 2000, Count: 17 },
      { Kind: "Commercials", Network: "General", Decade: 0, Count: 3 },
      { Kind: "Bumpers", Network: "Adult Swim", Decade: 2000, Count: 8 },
    ],
  });
  try {
    const w = dom.window;
    const summary = w.document.querySelector(
      "[data-ui=clip-library]",
    ).textContent;
    assert.match(summary, /Nickelodeon: 1990s 42 · 2000s 17/);
    assert.match(summary, /No network \(general\): no year 3/);
    assert.match(summary, /Bumpers.*Adult Swim: 2000s 8/);
    const shared = w.document.querySelector("[data-ui=shared-clips]");
    click(w, "Choose commercial folders or clips", shared);
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Done");
    click(w, "Save channels");
    await settle();
    assert.deepEqual(w.testApi.saved.SharedCommercials.FolderIds, [
      fixture.content.folders[0].Id,
    ]);
    assert.deepEqual(w.testApi.saved.Channels[0].Commercials.FolderIds, []);
  } finally {
    dom.window.close();
  }
});

test("the picture shape can restart players instead of adding black bars", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const select = [...w.document.querySelectorAll("main label.ltv-field")]
      .find((x) => x.textContent.includes("Picture shape"))
      .querySelector("select");
    change(w, select, "restart");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].PictureShape, "restart");
  } finally {
    dom.window.close();
  }
});

test("guide preview shows why each commercial and bumper was chosen", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    w.testApi.guide = [
      {
        Title: "Rugrats",
        Start: "2026-10-25T18:00:00Z",
        End: "2026-10-25T18:11:00Z",
        Commercial: false,
        Kind: "Program",
      },
      {
        Title: "Nick bump.mp4",
        Start: "2026-10-25T18:11:00Z",
        End: "2026-10-25T18:11:05Z",
        Commercial: true,
        Kind: "Bumper",
        Match: "Nickelodeon (studio “Nickelodeon”) · 1990s",
      },
      {
        Title: "Gak.mp4",
        Start: "2026-10-25T18:11:05Z",
        End: "2026-10-25T18:11:35Z",
        Commercial: true,
        Kind: "Commercial",
        Match:
          "general: no clips for this program's network · any year (program year unknown)",
      },
    ];
    click(w, "Guide preview");
    click(w, "Load guide");
    await settle();
    const items = [...w.document.querySelectorAll(".ltv-guide li")].map(
      (x) => x.textContent,
    );
    assert.match(items[0], /Rugrats/);
    assert.doesNotMatch(items[0], /Why/);
    assert.match(
      items[1],
      /Bumper.*Why: Nickelodeon \(studio “Nickelodeon”\) · 1990s/,
    );
    assert.match(items[2], /Commercial.*Why: general: no clips/);
  } finally {
    dom.window.close();
  }
});

test("captions are off by default and save their mode and language per channel", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const label = (text) =>
      [...w.document.querySelectorAll("main label.ltv-field")].find((x) =>
        x.textContent.includes(text),
      );
    const mode = label("Captions").querySelector("select");
    assert.equal(mode.value, "off");
    change(w, mode, "on");
    change(w, label("Caption language").querySelector("input"), " English ");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].Captions, "on");
    assert.equal(w.testApi.saved.Channels[0].CaptionLanguage, "English");
  } finally {
    dom.window.close();
  }
});
