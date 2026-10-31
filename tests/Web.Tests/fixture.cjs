const configuration = () => ({
  AutoChannels: false,
  AutoLibraryIds: [],
  GuideDays: 3,
  RefreshMinutes: 60,
  MaxActiveChannels: 2,
  TimeZoneId: "America/New_York",
  InternalBaseUrl: "http://127.0.0.1:8096",
  StreamToken: "private-test-token-".repeat(4),
  Channels: [
    {
      Id: "evening-tv",
      Name: "Evening TV",
      Number: "1",
      Enabled: true,
      Source: {
        LibraryIds: [],
        ItemIds: [],
        FolderIds: [],
        Genres: [],
        Tags: [],
        Shuffle: false,
        IncludeSpecials: false,
        RepeatWhenExhausted: true,
      },
      Blocks: [],
      Commercials: {
        LibraryIds: [],
        ItemIds: [],
        FolderIds: [],
        Genres: [],
        Tags: [],
        Shuffle: true,
        RepeatWhenExhausted: true,
      },
      CommercialCount: 0,
      AlwaysEncode: false,
    },
  ],
});
const content = {
  shows: [
    {
      Id: "11111111111111111111111111111111",
      Name: "Night Watch",
      Type: "Series",
      Year: 2022,
    },
    {
      Id: "22222222222222222222222222222222",
      Name: "Coastal Stories",
      Type: "Series",
      Year: 2020,
    },
  ],
  movies: [
    {
      Id: "33333333333333333333333333333333",
      Name: "The Long Weekend",
      Type: "Movie",
      Year: 2024,
    },
    {
      Id: "44444444444444444444444444444444",
      Name: "Beyond the Horizon",
      Type: "Movie",
      Year: 2023,
    },
  ],
  folders: [
    {
      Id: "55555555555555555555555555555555",
      Name: "Commercial Breaks",
      Type: "Folder",
    },
  ],
  videos: [],
};
function install(window, options = {}) {
  window.confirm = () => true;
  window.HTMLMediaElement.prototype.play = () => Promise.resolve();
  window.HTMLMediaElement.prototype.pause = () => {};
  if (!window.HTMLDialogElement.prototype.showModal)
    window.HTMLDialogElement.prototype.showModal = function () {
      this.open = true;
    };
  if (!window.HTMLDialogElement.prototype.close)
    window.HTMLDialogElement.prototype.close = function () {
      this.open = false;
    };
  const data = (window.testApi = {
    calls: [],
    saved: null,
    failSave: false,
    config: options.config || configuration(),
    clips: options.clips,
  });
  window.ApiClient = {
    getUrl: (path) => "/api/" + path,
    getCurrentUser: async () => ({
      Policy: { IsAdministrator: options.admin !== false },
    }),
    ajax: async (request) => {
      const path = request.url.replace("/api/LibraryTV/", "");
      data.calls.push(path);
      if (path === "configuration" && request.type === "POST") {
        if (data.holdSave) await data.holdSave;
        if (data.failSave) throw new Error("Save rejected");
        data.saved = JSON.parse(request.data);
        return { Saved: true };
      }
      if (path === "configuration") return structuredClone(data.config);
      if (path === "backup") {
        const { StreamToken, InternalBaseUrl, ...settings } = structuredClone(
          data.saved || data.config,
        );
        data.exported = { FormatVersion: 1, ...settings };
        return data.exported;
      }
      if (path === "backup/import") {
        const draft = JSON.parse(request.data);
        data.imported = draft;
        const { FormatVersion, StreamToken, InternalBaseUrl, ...settings } =
          draft;
        data.config = { ...data.config, ...settings };
        return { Saved: true };
      }
      if (path.startsWith("rebuild/")) {
        data.rebuilt = path;
        return { AppliedFrom: "2026-10-19T20:00:00Z" };
      }
      if (path === "libraries" && data.starting)
        throw {
          responseJSON: {
            Error: "Library TV is waiting for Jellyfin startup to finish.",
          },
        };
      if (path === "genres") return ["Animation", "Comedy", "Drama"];
      if (path === "libraries")
        return [
          { ItemId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Name: "TV Shows" },
          { ItemId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Name: "Movies" },
        ];
      if (path === "status" || path === "refresh")
        return {
          PluginPages:
            "Connected — Library TV Builder is registered in Plugin Pages.",
          Channels: data.healthChannels || [],
          ClipLibrary: data.clipLibrary || options.clipLibrary || [],
          CreditsScan: data.creditsScan || options.creditsScan,
          ClipMinimum: 8,
        };
      const searched = () => {
        const query = new URLSearchParams(path.split("?")[1] || "");
        const search = (query.get("search") || "").toLowerCase();
        return (data.clips || []).filter((x) => x.Title.toLowerCase().includes(search));
      };
      if (path.startsWith("clips/ids")) return searched().map((x) => x.Id);
      if (path.startsWith("clips/") && path.endsWith("/matches"))
        return (
          data.matches || {
            Summary: "Plays with programs from Nickelodeon:",
            Total: 3,
            Programs: ["Doug (1991)", "Rugrats (1991–2004)"],
          }
        );
      if (path.startsWith("clips?")) {
        const items = searched();
        return {
          Total: items.length,
          Items: items,
          Networks: [...new Set(items.map((x) => x.Network).filter(Boolean))],
          Decades: [...new Set(items.map((x) => Math.floor(x.Year / 10) * 10))],
        };
      }
      if (path === "media/resolve")
        return Object.values(content)
          .flat()
          .filter((item) => JSON.parse(request.data).includes(item.Id));
      if (path.startsWith("media?")) {
        const query = new URLSearchParams(path.split("?")[1]);
        let items = content[query.get("kind")] || [];
        const search = query.get("search").toLowerCase();
        items = items.filter((x) => x.Name.toLowerCase().includes(search));
        return { Items: items, TotalRecordCount: items.length };
      }
      if (path.startsWith("guide/")) return data.guide || [];
      throw new Error("Unexpected route: " + path);
    },
  };
}
module.exports = { configuration, content, install };
