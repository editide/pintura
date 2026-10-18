const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const wwwroot = path.join(__dirname, '..', 'Pintura', 'wwwroot');

// A page's classic scripts share one global scope, where a later function of the same name silently replaces an
// earlier one.
function collisions(scripts) {
  const owners = new Map();
  for (const script of scripts) {
    const source = fs.readFileSync(path.join(wwwroot, script), 'utf8');
    for (const [, name] of source.matchAll(/^(?:async\s+)?(?:function\*?|class|const|let|var)\s+([A-Za-z_$][\w$]*)/gm)) {
      owners.set(name, [...(owners.get(name) || []), script]);
    }
  }
  return [...owners].filter(([, files]) => files.length > 1).map(([name, files]) => `${name} in ${files.join(', ')}`);
}

function pageScripts(page) {
  const html = fs.readFileSync(path.join(wwwroot, page), 'utf8');
  return [...html.matchAll(/<script src="([^"]+)"/g)].map(([, src]) => src);
}

function exportScripts() {
  const source = fs.readFileSync(path.join(__dirname, '..', 'Pintura', 'HtmlExport.cs'), 'utf8');
  const list = source.match(/Scripts = \[([^\]]+)\]/)[1];
  return [...list.matchAll(/"([^"]+)"/g)].map(([, script]) => script);
}

test('no two scripts on the page define the same top-level name', () => {
  assert.deepEqual(collisions(pageScripts('index.html')), []);
});

test('no two scripts on the headless page define the same top-level name', () => {
  assert.deepEqual(collisions(pageScripts('headless.html')), []);
});

test('no two scripts in an exported page define the same top-level name', () => {
  assert.deepEqual(collisions(exportScripts()), []);
});
