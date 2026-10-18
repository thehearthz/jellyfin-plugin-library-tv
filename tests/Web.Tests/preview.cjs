// Local visual QA fixture, never used or shipped by the plugin at runtime.
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const fixture = require("./fixture.cjs");
const html = fs.readFileSync(
  path.join(__dirname, "../../src/LibraryTV/Web/config.html"),
  "utf8",
);
const initial = `<script>const configuration=${fixture.configuration.toString()};const content=${JSON.stringify(fixture.content)};(${fixture.install.toString()})(window);</script>`;
http
  .createServer((req, res) => {
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    res.end(
      '<!doctype html><html><head><title>Library TV Builder Preview</title><meta name="viewport" content="width=device-width, initial-scale=1"><style>body{margin:0;background:#11171b;color:#edf1f4;font:16px system-ui}a{color:#6ddac2}</style></head><body>' +
        initial +
        html +
        "</body></html>",
    );
  })
  .listen(18766, "127.0.0.1", () =>
    console.log("Builder preview: http://127.0.0.1:18766"),
  );
