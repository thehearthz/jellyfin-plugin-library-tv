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
    const cells = (label) =>
      [...w.document.querySelectorAll("[data-ui=clip-library] tr")]
        .find((row) => row.querySelector("th").textContent === label)
        .children;
    const text = (label) => [...cells(label)].map((x) => x.textContent).join(" ");
    assert.equal(text("Nickelodeon"), "Nickelodeon – 42 17 59");
    assert.equal(text("No network (general)"), "No network (general) 3 – – 3");
    assert.equal(text("Adult Swim"), "Adult Swim 8 8");
    assert.match(summary, /Highlighted cells have fewer than 8 clips/);
    assert.equal(
      [...w.document.querySelectorAll("[data-ui=clip-library] td.ltv-thin")].length,
      1,
    );
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

test("the preferred audio language saves per channel", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const input = [...w.document.querySelectorAll("main label.ltv-field")]
      .find((x) => x.textContent.includes("Preferred audio language"))
      .querySelector("input");
    assert.equal(input.value, "");
    change(w, input, " Japanese ");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].AudioLanguage, "Japanese");
  } finally {
    dom.window.close();
  }
});

test("credits speed, automatic channel breaks and new channel breaks save", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const credits = [...w.document.querySelectorAll("main label.ltv-field")]
      .find((x) => x.textContent.includes("Credits"))
      .querySelector("select");
    assert.equal(credits.value, "1");
    change(w, credits, "4");
    const automatic = w.document.querySelector(
      "[data-setting=AutomaticCommercialCount]",
    );
    assert.equal(automatic.value, "2");
    change(w, automatic, "3");
    click(w, "+ New channel");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.Channels[0].CreditsSpeed, 4);
    assert.equal(w.testApi.saved.AutomaticCommercialCount, 3);
    assert.equal(w.testApi.saved.Channels[1].CommercialCount, 2);
  } finally {
    dom.window.close();
  }
});

test("guide preview marks credits that are sped up", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    w.testApi.guide = [
      {
        Title: "Show",
        Start: "2026-10-26T18:00:00Z",
        End: "2026-10-26T18:20:00Z",
        Commercial: false,
        Kind: "Program",
        Speed: 1,
      },
      {
        Title: "Show",
        Start: "2026-10-26T18:20:00Z",
        End: "2026-10-26T18:20:25Z",
        Commercial: false,
        Kind: "Program",
        Speed: 4,
      },
    ];
    click(w, "Guide preview");
    click(w, "Load guide");
    await settle();
    const items = [...w.document.querySelectorAll(".ltv-guide li")].map(
      (x) => x.textContent,
    );
    assert.doesNotMatch(items[0], /Credits/);
    assert.match(items[1], /Credits 4×/);
  } finally {
    dom.window.close();
  }
});

test("credits detection can be turned off and shows its scan progress", async () => {
  const dom = await ready({
    creditsScan: {
      Enabled: true,
      Running: true,
      Programs: 40,
      Scanned: 12,
      Found: 9,
      Remaining: 28,
      Current: "Doug — S01E03",
    },
  });
  try {
    const w = dom.window;
    const status = w.document.querySelector("[data-ui=credits-scan]");
    assert.match(status.textContent, /28 of 40 programs left/);
    assert.match(status.textContent, /now reading Doug — S01E03/);
    assert.match(status.textContent, /Credits found in 9 of 12 scanned files/);
    const toggle = w.document.querySelector("[data-setting=DetectCredits]");
    assert.equal(toggle.checked, true);
    toggle.checked = false;
    toggle.dispatchEvent(new w.Event("change", { bubbles: true }));
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.DetectCredits, false);
  } finally {
    dom.window.close();
  }
});

const clipRows = [
  { Id: "c1000000000000000000000000000001", Title: "Nickelodeon Gak 1994", Path: "/ads/Nickelodeon Gak 1994.mp4", Seconds: 30, Kind: "Commercial", Network: "Nickelodeon", Year: 1994, Show: "", Disabled: false, Sources: [], Why: "Filed under Nickelodeon in the 1990s." },
  { Id: "c2000000000000000000000000000002", Title: "Cola", Path: "/ads/Cola.mp4", Seconds: 30, Kind: "Commercial", Network: "", Year: 0, Show: "", Disabled: false, Sources: [], Why: "General: plays with programs whose network has no clips of its own." },
  { Id: "c3000000000000000000000000000003", Title: "Cereal bump", Path: "/ads/Cereal bump.mp4", Seconds: 10, Kind: "Commercial", Network: "", Year: 2001, Show: "", Disabled: false, Sources: [], Why: "General." },
];
function pickRow(w, title) {
  const row = [...w.document.querySelectorAll(".ltv-clip")].find((x) =>
    x.querySelector(".ltv-clip-title").textContent === title,
  );
  assert.ok(row, "Clip " + title);
  return row;
}

test("the clip browser lists clips, edits one, previews it and shows where it plays", async () => {
  const dom = await ready({ clips: clipRows });
  try {
    const w = dom.window;
    const names = [...w.document.querySelectorAll(".ltv-clip-title")].map((x) => x.textContent);
    assert.deepEqual(names, ["Nickelodeon Gak 1994", "Cola", "Cereal bump"]);
    assert.match(pickRow(w, "Cola").textContent, /General.*no year/);
    pickRow(w, "Cola").querySelector(".ltv-clip-title").click();
    const detail = w.document.querySelector(".ltv-clip-detail");
    assert.match(detail.textContent, /General: plays with programs/);
    const inputs = detail.querySelectorAll("input");
    change(w, inputs[0], "Adult Swim");
    change(w, inputs[1], "2004");
    assert.match(pickRow(w, "Cola").textContent, /Adult Swim.*2004.*Unsaved/);
    click(w, "Where it plays", detail);
    await settle();
    assert.match(w.document.querySelector("[data-ui=clip-matches]").textContent, /Doug \(1991\) · Rugrats \(1991–2004\) … and 1 more/);
    click(w, "Preview", w.document.querySelector(".ltv-clip-detail"));
    assert.match(w.document.querySelector("[data-ui=preview-video]").getAttribute("src"), /Videos\/c2000000000000000000000000000002\/stream\?static=true/);
    click(w, "Close", w.document.querySelector("[data-ui=preview]"));
    click(w, "Save channels");
    await settle();
    assert.deepEqual(
      w.testApi.saved.ClipOverrides.map((x) => [x.ItemId, x.Network, x.Year]),
      [["c2000000000000000000000000000002", "Adult Swim", 2004]],
    );
  } finally {
    dom.window.close();
  }
});

test("clips can be searched, selected together and changed in bulk, then undone", async () => {
  const dom = await ready({ clips: clipRows });
  try {
    const w = dom.window;
    const search = w.document.querySelector("[data-ui=clip-browser] input[type=search]");
    search.value = "e";
    search.dispatchEvent(new w.Event("input", { bubbles: true }));
    await new Promise((r) => setTimeout(r, 400));
    await settle();
    assert.equal(w.document.querySelectorAll(".ltv-clip").length, 2);
    click(w, "Select all 2 matching", w.document.querySelector("[data-ui=clip-browser]"));
    await settle();
    const bulk = w.document.querySelector("[data-ui=clip-browser] .ltv-block");
    assert.match(bulk.textContent, /2 selected/);
    change(w, bulk.querySelector("input[list]"), "Toonami");
    change(w, bulk.querySelector("select"), "bumper");
    click(w, "Apply to 2 selected", bulk);
    assert.match(pickRow(w, "Nickelodeon Gak 1994").textContent, /Toonami/);
    assert.match(pickRow(w, "Nickelodeon Gak 1994").textContent, /Bumper/);
    click(w, "Turn off", w.document.querySelector("[data-ui=clip-browser] .ltv-block"));
    assert.match(pickRow(w, "Cereal bump").textContent, /Off/);
    click(w, "Save channels");
    await settle();
    const saved = w.testApi.saved.ClipOverrides;
    assert.equal(saved.length, 2);
    assert.ok(saved.every((x) => x.Network === "Toonami" && x.Kind === "bumper" && x.Disabled === true));
    click(w, "Undo my edits", w.document.querySelector("[data-ui=clip-browser] .ltv-block"));
    click(w, "Save channels");
    await settle();
    assert.deepEqual(w.testApi.saved.ClipOverrides, []);
  } finally {
    dom.window.close();
  }
});

test("rules re-file clips by name or folder and are checked before saving", async () => {
  const dom = await ready({ clips: clipRows });
  try {
    const w = dom.window;
    click(w, "+ Treat clips named “bump” as bumpers");
    const rules = w.document.querySelector("[data-ui=clip-rules]");
    assert.equal(rules.querySelectorAll(".ltv-block").length, 1);
    pickRow(w, "Cola").querySelector(".ltv-clip-title").click();
    click(w, "Make a rule for this folder", w.document.querySelector(".ltv-clip-detail"));
    assert.equal(rules.querySelectorAll(".ltv-block").length, 2);
    assert.equal(rules.querySelectorAll("input")[0].value, "bump");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.ClipRules[1].Contains, "/ads/");
    assert.deepEqual([w.testApi.saved.ClipRules[0].Contains, w.testApi.saved.ClipRules[0].Kind], ["bump", "bumper"]);
    change(w, rules.querySelectorAll("input")[0], "  ");
    w.testApi.saved = null;
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved, null);
    assert.match(w.document.querySelector("[data-ui=status]").textContent, /needs some text/);
  } finally {
    dom.window.close();
  }
});

function typeInto(window, input, value) {
  input.value = value;
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
}

function selectLabel(window, label, container = window.document) {
  const fields = [...container.querySelectorAll("label.ltv-field")];
  const field =
    fields.find((x) => x.querySelector("span").textContent === label) ||
    fields.find((x) => x.querySelector("span").textContent.startsWith(label));
  assert.ok(field, "Field " + label);
  return field.querySelector("select, input, textarea");
}

test("block timing, padding and break placement settings are saved", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Schedule");
    click(w, "+ Add time block");
    change(w, selectLabel(w, "Start", w.document.querySelector(".ltv-block")), "exact");
    change(w, selectLabel(w, "Fill", w.document.querySelector(".ltv-block")), "fit");
    change(w, selectLabel(w, "Skip the block", w.document.querySelector(".ltv-block")), "15");
    change(w, selectLabel(w, "Guide title", w.document.querySelector(".ltv-block")), "Saturday Morning Cartoons");
    click(w, "Commercials");
    change(w, selectLabel(w, "Start programs on the clock"), "30");
    change(w, selectLabel(w, "Up to this many clips"), "4");
    change(w, selectLabel(w, "Breaks inside a program fall"), "interval");
    change(w, selectLabel(w, "Minutes between breaks"), "20");
    change(w, selectLabel(w, "Keep the last minutes"), "5");
    click(w, "Save channels");
    await settle();
    const c = w.testApi.saved.Channels[0];
    assert.equal(c.PadMinutes, 30);
    assert.equal(c.CommercialCountMax, 4);
    assert.equal(c.BreakMode, "interval");
    assert.equal(c.BreakIntervalMinutes, 20);
    assert.equal(c.BreakFreeEndMinutes, 5);
    assert.deepEqual(
      [c.Blocks[0].StartMode, c.Blocks[0].Fill, c.Blocks[0].MaxLateMinutes, c.Blocks[0].GuideTitle],
      ["exact", "fit", 15, "Saturday Morning Cartoons"],
    );
  } finally {
    dom.window.close();
  }
});

test("playback order, filters, per-show turns and channel images are saved", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    change(w, selectLabel(w, "Order"), "chronological");
    change(w, selectLabel(w, "Episodes in a row for particular shows"), "Night Watch = 3\nnot a line");
    change(w, selectLabel(w, "Highest content rating"), "PG");
    change(w, selectLabel(w, "Earliest year"), "1980");
    change(w, selectLabel(w, "Latest year"), "1999");
    change(w, selectLabel(w, "Never include genres"), "Horror, Reality");
    change(w, selectLabel(w, "Title contains any of"), "night, watch");
    change(w, selectLabel(w, "Title contains none of"), "Reunion");
    change(w, selectLabel(w, "Watermark drawn"), "/media/logo.png");
    change(w, selectLabel(w, "Preferred audio track title"), "Dolby");
    click(w, "Save channels");
    await settle();
    const c = w.testApi.saved.Channels[0];
    assert.equal(c.Source.Order, "chronological");
    assert.deepEqual(c.Source.Turns, [{ Show: "Night Watch", Episodes: 3 }]);
    assert.equal(c.Source.MaxRating, "PG");
    assert.deepEqual([c.Source.MinYear, c.Source.MaxYear], [1980, 1999]);
    assert.deepEqual(c.Source.ExcludeGenres, ["Horror", "Reality"]);
    assert.deepEqual(c.Source.TitleContains, ["night", "watch"]);
    assert.deepEqual(c.Source.TitleExcludes, ["Reunion"]);
    assert.equal(c.Watermark, "/media/logo.png");
    assert.equal(c.AudioTitle, "Dolby");
    assert.equal(c.WatermarkPosition, "bottom-right");
  } finally {
    dom.window.close();
  }
});

test("the picker sends genre and year filters with its search", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "Choose shows & movies");
    await settle();
    typeInto(w, w.document.querySelector("[data-ui=genre-filter]"), "Comedy");
    typeInto(w, w.document.querySelector("[data-ui=year-from]"), "1990");
    typeInto(w, w.document.querySelector("[data-ui=year-to]"), "1999");
    await new Promise((r) => setTimeout(r, 600));
    await settle();
    const last = w.testApi.calls.filter((x) => x.includes("media?")).pop();
    assert.match(last, /genre=Comedy/);
    assert.match(last, /minYear=1990/);
    assert.match(last, /maxYear=1999/);
  } finally {
    dom.window.close();
  }
});

test("smart channels save genres, decades, years and chosen titles", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    click(w, "+ Add smart channel");
    const box = () => w.document.querySelector("[data-ui=smart-channels] .ltv-block");
    change(w, selectLabel(w, "Channel name", box()), "Nineties comedy");
    const tick = (label) => {
      const box_ = [...box().querySelectorAll("label.ltv-check")].find((x) => x.textContent.trim() === label);
      assert.ok(box_, "Option " + label);
      const input = box_.querySelector("input");
      input.checked = true;
      input.dispatchEvent(new w.Event("change", { bubbles: true }));
    };
    tick("Movies");
    tick("Comedy");
    tick("Animation");
    tick("1990s");
    tick("2000s");
    change(w, selectLabel(w, "Specific years", box()), "1985, 1994-1996");
    click(w, "Choose shows & movies", box());
    await settle();
    click(w, "+ Select", w.document.querySelector(".ltv-media"));
    click(w, "Done");
    click(w, "Save channels");
    await settle();
    const smart = w.testApi.saved.SmartChannels[0];
    assert.equal(smart.Name, "Nineties comedy");
    assert.deepEqual(smart.LibraryIds, ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]);
    assert.deepEqual(smart.Genres, ["Comedy", "Animation"]);
    assert.deepEqual(smart.Decades, [1990, 2000]);
    assert.deepEqual(smart.Years, [1985, 1994, 1995, 1996]);
    assert.equal(smart.ItemIds.length, 1);
  } finally {
    dom.window.close();
  }
});

test("encoding quality and height are saved", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    change(w, selectLabel(w, "Encoding quality"), "high");
    change(w, selectLabel(w, "Tallest encoded picture"), "1080");
    click(w, "Save channels");
    await settle();
    const c = w.testApi.saved.Channels[0];
    assert.equal(c.EncodeQuality, "high");
    assert.equal(c.EncodeHeight, 1080);
  } finally {
    dom.window.close();
  }
});

test("credits speed for automatic channels is saved", async () => {
  const dom = await ready();
  try {
    const w = dom.window;
    const select = w.document.querySelector("[data-setting=AutomaticCreditsSpeed]");
    change(w, select, "4");
    click(w, "Save channels");
    await settle();
    assert.equal(w.testApi.saved.AutomaticCreditsSpeed, 4);
  } finally {
    dom.window.close();
  }
});
