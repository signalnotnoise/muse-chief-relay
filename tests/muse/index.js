// Node 22's test runner treats `node --test tests/muse/` as one directory
// entry and loads this file. The individual *.test.js files still run on
// their own; this only pulls them in for the directory invocation.
const fs = require("fs");
const path = require("path");

if (path.basename(process.argv[1] || "") !== "index.js") {
  for (const name of fs.readdirSync(__dirname).sort()) {
    if (name.endsWith(".test.js")) require(path.join(__dirname, name));
  }
}
