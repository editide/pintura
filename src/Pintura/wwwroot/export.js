'use strict';

// The viewer of a presentation exported to one HTML file: the deck is inlined beside this script, so it needs no
// server, and it only shows: a sidebar of thumbnails beside one slide at fit, or the slides presented full screen.
// Each book the deck reads is inlined as Libreta's own exported page, which a traced number opens over the deck.

const THUMBNAIL_WIDTH = 170;

const deck = JSON.parse(document.getElementById('deck').textContent);
const layout = document.querySelector('.layout');
const thumbnails = document.getElementById('thumbnails');
const slideArea = document.getElementById('slide');
const present = document.getElementById('present');
const state = { current: 0, presenting: false };
const books = JSON.parse(document.getElementById('books')?.textContent ?? '{}');
const bookUrls = {};
let wheelTotal = 0;

function indexOf(id) {
  return Math.max(0, deck.slides.findIndex((slide) => slide.id === id));
}

function renderSidebar() {
  document.getElementById('deck-name').textContent = deck.name;
  const byId = new Map(deck.slides.map((slide) => [slide.id, slide]));
  for (const section of deck.sections) {
    if (section.name) {
      element('div', 'section-name', thumbnails).textContent = section.name;
    }
    for (const id of section.slides) {
      const slide = byId.get(id);
      const button = element('button', 'thumbnail', thumbnails);
      button.type = 'button';
      button.dataset.slide = id;
      button.title = slide.title || id;
      element('span', 'number', button).textContent = slide.number;
      mountStage(element('div', 'preview', button), slide, deck.size, THUMBNAIL_WIDTH);
      button.addEventListener('click', () => go(indexOf(id)));
    }
  }
}

function renderCurrent() {
  const id = deck.slides[state.current].id;
  for (const button of thumbnails.querySelectorAll('.thumbnail')) {
    const current = button.dataset.slide === id;
    button.classList.toggle('current', current);
    if (current) {
      button.scrollIntoView({ block: 'nearest' });
    }
  }
}

function renderSlide() {
  const frame = element('div', 'slide-frame');
  slideArea.replaceChildren(frame);
  const room = slideArea.getBoundingClientRect();
  const [width, height] = deck.size;
  mountStage(frame, deck.slides[state.current], deck.size, Math.min(room.width, room.height * width / height));
  frame.addEventListener('click', (event) => {
    if (event.target.closest('a')) {
      return;
    }
    const traced = event.target.closest('[data-trace]');
    const trace = traced && JSON.parse(traced.dataset.trace);
    if (trace && trace.book in books) {
      openBook(trace);
      return;
    }
    step(1);
  });
}

// A presentation with no slides shows only its name.
function render() {
  if (deck.slides.length === 0) {
    return;
  }
  renderSlide();
  renderCurrent();
  renderPresent();
}

function renderPresent() {
  if (!state.presenting || deck.slides.length === 0) {
    return;
  }
  const [width, height] = deck.size;
  const frame = element('div', 'present-frame');
  present.replaceChildren(frame);
  mountStage(frame, deck.slides[state.current], deck.size, Math.min(innerWidth, innerHeight * width / height));
}

function setNav(shown) {
  layout.classList.toggle('nav-hidden', !shown);
  document.getElementById('nav-show').hidden = shown;
  render();
}

// The book's page selects the cell its fragment names, and selects the next without reloading when only that changes.
function openBook(trace) {
  showInBook(`${bookUrl(trace.book)}#${new URLSearchParams({ line: trace.line, column: trace.column })}`);
  showBook(deck.slides[state.current]);
}

function bookUrl(name) {
  return bookUrls[name] ??= URL.createObjectURL(new Blob([books[name]], { type: 'text/html' }));
}

// A book takes a moment to read, so it starts on a hover over its numbers, and the click that follows only moves it.
function preloadBook(event) {
  const traced = event.target.closest('[data-trace]');
  const name = traced && JSON.parse(traced.dataset.trace).book;
  if (name in books && !bookFrame.dataset.page?.startsWith(bookUrl(name))) {
    showInBook(bookUrl(name));
  }
}

function go(index) {
  if (deck.slides.length === 0) {
    return;
  }
  state.current = Math.min(Math.max(index, 0), deck.slides.length - 1);
  history.replaceState(null, '', `#${encodeURIComponent(deck.slides[state.current].id)}`);
  render();
}

function step(offset) {
  go(state.current + offset);
}

// Each notch of a mouse wheel turns a slide; a trackpad's small deltas add up to one.
function turnOnWheel(event) {
  // Firefox counts a mouse's notches in lines rather than pixels.
  wheelTotal += event.deltaMode === WheelEvent.DOM_DELTA_LINE ? event.deltaY * 16 : event.deltaY;
  if (Math.abs(wheelTotal) >= 40) {
    step(Math.sign(wheelTotal));
    wheelTotal = 0;
  }
}

function startPresenting() {
  state.presenting = true;
  present.hidden = false;
  if (present.requestFullscreen) {
    present.requestFullscreen().catch(() => {});
  }
  renderPresent();
}

function stopPresenting() {
  state.presenting = false;
  present.hidden = true;
  if (document.fullscreenElement) {
    document.exitFullscreen().catch(() => {});
  }
}

present.addEventListener('click', () => step(1));
present.addEventListener('wheel', turnOnWheel, { passive: true });
document.getElementById('view').addEventListener('wheel', turnOnWheel, { passive: true });
document.getElementById('present-button').addEventListener('click', startPresenting);
document.getElementById('nav-hide').addEventListener('click', () => setNav(false));
document.getElementById('nav-show').addEventListener('click', () => setNav(true));

document.addEventListener('fullscreenchange', () => {
  if (!document.fullscreenElement && state.presenting) {
    stopPresenting();
  }
});

document.addEventListener('keydown', (event) => {
  if (event.ctrlKey || event.metaKey || event.altKey) {
    return;
  }
  // The deck lies hidden under an open book, so only closing it applies.
  if (bookShown()) {
    if (event.key === 'Escape') {
      closeBook();
      event.preventDefault();
    }
    return;
  }
  const offsets = { ArrowRight: 1, ArrowDown: 1, PageDown: 1, ' ': 1, ArrowLeft: -1, ArrowUp: -1, PageUp: -1 };
  const commands = {
    Home: () => go(0),
    End: () => go(deck.slides.length - 1),
    Escape: () => state.presenting && stopPresenting(),
    '\\': () => setNav(layout.classList.contains('nav-hidden')),
    p: () => !state.presenting && startPresenting(),
    F5: () => !state.presenting && startPresenting()
  };
  if (event.key in offsets) {
    step(offsets[event.key]);
  } else if (event.key in commands) {
    commands[event.key]();
  } else {
    return;
  }
  event.preventDefault();
});

window.addEventListener('resize', render);

if (book) {
  document.body.classList.add('tracing');
  slideArea.addEventListener('pointerover', preloadBook);
}

state.current = indexOf(decodeURIComponent(location.hash.slice(1)));
renderSidebar();
render();
// Text is fitted as it is drawn, so fonts that arrive afterwards need the slides fitted again.
useFonts(deck.fonts).then(() => document.fonts.ready).then(render);