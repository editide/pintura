'use strict';

// Driven by Playwright rather than a person: each call fetches the presentation afresh, draws, and resolves once
// fonts and pictures have loaded and shrunk text has been fitted, so a screenshot or a measurement sees the finished
// slide. Nothing here holds the event stream, so this page never keeps a server alive.

const SHEET_WIDTH = 480;

const root = document.getElementById('root');

async function fetchDeck() {
  const response = await fetch('api/presentation', { cache: 'no-store' });
  const deck = await response.json();
  await useFonts(deck.fonts);
  return deck;
}

async function settle() {
  await document.fonts.ready;
  await Promise.all([...root.querySelectorAll('.item')].map((node) => node.loaded));
  for (const stage of root.querySelectorAll('.stage')) {
    fitText(stage);
  }
}

async function showSlide(id) {
  const deck = await fetchDeck();
  root.replaceChildren(renderStage(deck.slides.find((slide) => slide.id === id), deck.size));
  await settle();
}

function showSheetOf(deck) {
  const columns = deck.slides.length <= 4 ? 2 : deck.slides.length <= 12 ? 3 : 4;
  const sheet = element('div', 'sheet');
  sheet.style.gridTemplateColumns = `repeat(${columns}, ${SHEET_WIDTH}px)`;
  const byId = new Map(deck.slides.map((slide) => [slide.id, slide]));
  for (const section of deck.sections) {
    if (section.name) {
      element('div', 'sheet-section', sheet).textContent = section.name;
    }
    for (const id of section.slides) {
      const slide = byId.get(id);
      const cell = element('div', 'sheet-cell', sheet);
      mountStage(element('div', 'sheet-frame', cell), slide, deck.size, SHEET_WIDTH);
      element('div', 'sheet-caption', cell).textContent = `${slide.number} · ${slide.id}`;
    }
  }
  root.replaceChildren(sheet);
}

async function showSheet() {
  showSheetOf(await fetchDeck());
  await settle();
}

// Every slide at its own size, one after another, for the browser to print a page each.
async function showPrint() {
  const deck = await fetchDeck();
  root.replaceChildren(...deck.slides.map((slide) => renderStage(slide, deck.size)));
  await settle();
}

function families(deck) {
  const found = new Set();
  const visit = (items) => {
    for (const item of items || []) {
      if (item.chart) {
        found.add(item.chart.text.font);
      }
      const texts = [item.text, ...(item.table ? item.table.cells.map((cell) => cell.text) : [])].filter(Boolean);
      for (const paragraph of texts.flatMap((text) => text.paragraphs)) {
        for (const run of [paragraph.mark, ...paragraph.runs]) {
          if (run.font) {
            found.add(run.font);
          }
        }
      }
    }
  };
  deck.slides.forEach((slide) => visit(slide.items));
  return [...found].sort();
}

async function measure(ids) {
  const deck = await fetchDeck();
  const slides = deck.slides.filter((slide) => !ids || ids.includes(slide.id));
  const stages = slides.map((slide) => [slide.id, renderStage(slide, deck.size)]);
  root.replaceChildren(...stages.map(([, stage]) => stage));
  await settle();
  return {
    fonts: families(deck).map((family) => ({ family, available: fontAvailable(family) })),
    boxes: stages.flatMap(([slide, stage]) => measureText(stage).map((box) => ({ slide, ...box }))),
    charts: stages.flatMap(([slide, stage]) => measureCharts(stage).map((chart) => ({ slide, ...chart })))
  };
}

// compare.js reads the page's state: what the revision drew and what changed, and which side is showing.
const state = { compare: null, before: false };

// Each slide the change touched, or the one asked for, as the revision drew it beside the working tree, with the
// changes marked on both as C marks them in the preview. Answers what went wrong, or null once drawn.
async function showCompare(id, base) {
  const [deck, response] = await Promise.all([fetchDeck(), fetch(`api/compare?base=${encodeURIComponent(base)}`,
    { cache: 'no-store' })]);
  const compare = await response.json();
  if (!response.ok) {
    return compare.error || `comparing failed (${response.status})`;
  }
  state.compare = compare;
  const after = (slideId) => deck.slides.find((slide) => slide.id === slideId) || null;
  const ids = id ? [id] : compare.diff.slides.filter((slide) => slide.status !== 'moved').map((slide) => slide.id);
  if (id && !after(id) && !beforeSlide(id)) {
    return `no slide '${id}' in the working tree or ${baseName()}`;
  }
  if (ids.length === 0) {
    return `no slide changed since ${baseName()}`;
  }
  const width = id ? deck.size[0] : SHEET_WIDTH;
  const sheet = element('div', 'sheet');
  sheet.style.gridTemplateColumns = `repeat(2, ${width}px)`;
  const sides = [];
  for (const slideId of ids) {
    for (const [before, slide, name] of [[true, beforeSlide(slideId), baseName()],
      [false, after(slideId), 'the working tree']]) {
      const cell = element('div', 'sheet-cell', sheet);
      const frame = element('div', 'sheet-frame', cell);
      element('div', 'sheet-caption', cell).textContent = `${slide ? `${slide.number} · ` : ''}${slideId} · ${name}`;
      sides.push({ before, slide, name, frame, marked: after(slideId) || beforeSlide(slideId) });
    }
  }
  root.replaceChildren(sheet);
  for (const side of sides) {
    side.stage = mountStage(side.frame, side.slide || { items: [], background: '#fff' }, deck.size, width);
    if (!side.slide) {
      element('div', 'absent', side.stage).textContent = `Not in ${side.name}`;
    }
  }
  await settle();
  for (const side of sides) {
    state.before = side.before;
    renderChanges(side.marked, side.stage);
  }
  return null;
}

window.pintura = { showSlide, showSheet, showPrint, showCompare, measure };