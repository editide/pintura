'use strict';

// The viewer of a presentation exported to one HTML file: the deck is inlined beside this script, so it needs no
// server, and it only shows: a sidebar of thumbnails beside one slide at fit, or the slides presented full screen.
// Each book the deck reads is inlined as Libreta's own exported page, which a traced number opens over the deck: a
// click, or on a phone, where a tap turns the slide, a long press.

const THUMBNAIL_WIDTH = 170;
// Below this width the sidebar opens over the slide rather than beside it, as app.css's breakpoint does.
const NARROW = matchMedia('(max-width: 52rem)');
// How long the presenter's controls stay after the last touch or move of the pointer.
const CONTROLS_MS = 3000;
const LONG_PRESS_MS = 500;
// How far a pointer moves before its press is a drag rather than a click or a long press.
const DRAG_PX = 6;
// The hint that numbers trace stops once the reader has traced one, closed it, or seen it on this many slides.
const HINT_SLIDES = 3;
const COARSE = matchMedia('(pointer: coarse)');

const deck = JSON.parse(document.getElementById('deck').textContent);
const layout = document.querySelector('.layout');
const thumbnails = document.getElementById('thumbnails');
const slideArea = document.getElementById('slide');
const present = document.getElementById('present');
const counter = document.getElementById('counter');
const state = { current: 0, presenting: false };
const books = JSON.parse(document.getElementById('books')?.textContent ?? '{}');
const bookUrls = {};
// The pointer pressed on the slide, which decides what its click does.
let press = null;
const hintSlides = new Set();
let hintDone = stored('traceHint') === 'done';
let wheelTotal = 0;
let touchStart = null;
let controlsTimer = 0;

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
      button.addEventListener('click', () => {
        go(indexOf(id));
        if (NARROW.matches) {
          setNav(false);
        }
      });
    }
  }
}

// Nothing marks a traced number until the pointer is over it, and a finger never hovers, so a pill says which
// numbers trace, underlining them, until the reader has the idea. Typed numbers turn the slide like the rest of it.
function renderHint() {
  const hint = document.getElementById('trace-hint');
  const id = deck.slides[state.current].id;
  const traced = [...slideArea.querySelectorAll('[data-trace]')].some((node) => traceAt(node));
  const shown = !hintDone && traced && (hintSlides.size < HINT_SLIDES || hintSlides.has(id));
  hint.hidden = !shown;
  document.body.classList.toggle('hinting', shown);
  if (!shown) {
    return;
  }
  hintSlides.add(id);
  document.getElementById('trace-hint-text').textContent = COARSE.matches
    ? 'Touch and hold an underlined number to see where it comes from'
    : 'Click an underlined number to see where it comes from, or hold Ctrl to underline them later';
}

// A reader who has traced a number or closed the hint has no need of it in any deck again.
function endHint() {
  hintDone = true;
  store('traceHint', 'done');
  document.getElementById('trace-hint').hidden = true;
  document.body.classList.remove('hinting');
}

// A file opened from disk may have no storage, which leaves the hint to show again next time.
function stored(key) {
  try {
    return localStorage.getItem(`pintura.${key}`);
  } catch {
    return null;
  }
}

function store(key, value) {
  try {
    localStorage.setItem(`pintura.${key}`, value);
  } catch {}
}

function renderCurrent() {
  const id = deck.slides[state.current].id;
  counter.textContent = `${state.current + 1} / ${deck.slides.length}`;
  document.getElementById('previous').disabled = state.current === 0;
  document.getElementById('next').disabled = state.current === deck.slides.length - 1;
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
    // A long press has already opened the book.
    if (event.target.closest('a') || press?.held) {
      return;
    }
    const trace = traceAt(event.target);
    const action = clickAction({
      pointer: press?.pointer ?? 'mouse',
      traced: Boolean(trace),
      selecting: Boolean(press?.selecting) || !getSelection().isCollapsed,
      dragged: Boolean(press?.dragged)
    });
    if (action === 'trace') {
      openBook(trace);
    } else if (action === 'step') {
      step(tapStep(event.clientX, frame.getBoundingClientRect()));
    }
  });
}

function traceAt(target) {
  const traced = target.closest('[data-trace]');
  const trace = traced && JSON.parse(traced.dataset.trace);
  return trace && trace.book in books ? trace : null;
}

// Read before the press's default action, which collapses the selection a click would only clear. A finger on a
// number starts its book reading, which the long press then only moves to the cell.
function startPress(event) {
  press = { pointer: event.pointerType, x: event.clientX, y: event.clientY, selecting: !getSelection().isCollapsed };
  const trace = event.pointerType === 'touch' && traceAt(event.target);
  if (trace) {
    preload(trace.book);
    const pressed = press;
    pressed.timer = setTimeout(() => {
      pressed.held = true;
      openBook(trace);
    }, LONG_PRESS_MS);
  }
}

function movePress(event) {
  if (press && Math.hypot(event.clientX - press.x, event.clientY - press.y) > DRAG_PX) {
    press.dragged = true;
    clearTimeout(press.timer);
  }
}

function endPress() {
  clearTimeout(press?.timer);
}

// A presentation with no slides shows only its name.
function render() {
  if (deck.slides.length === 0) {
    return;
  }
  renderSlide();
  renderCurrent();
  renderHint();
  renderPresent();
}

function renderPresent() {
  if (!state.presenting || deck.slides.length === 0) {
    return;
  }
  const [width, height] = deck.size;
  const frame = element('div', 'present-frame');
  present.querySelector('.present-frame')?.remove();
  present.prepend(frame);
  mountStage(frame, deck.slides[state.current], deck.size, Math.min(innerWidth, innerHeight * width / height));
}

function setNav(shown) {
  layout.classList.toggle('nav-hidden', !shown);
  document.getElementById('nav-show').hidden = shown;
  render();
}

// The book's page selects the cell its fragment names, and selects the next without reloading when only that changes.
function openBook(trace) {
  endHint();
  showInBook(`${bookUrl(trace.book)}#${new URLSearchParams({ line: trace.line, column: trace.column })}`);
  showBook(deck.slides[state.current]);
}

function bookUrl(name) {
  return bookUrls[name] ??= URL.createObjectURL(new Blob([books[name]], { type: 'text/html' }));
}

// A book takes a moment to read, so it starts on a hover over its numbers, and the click that follows only moves it.
function preloadBook(event) {
  const trace = event.pointerType !== 'touch' && traceAt(event.target);
  if (trace) {
    preload(trace.book);
  }
}

function preload(name) {
  if (!bookFrame.dataset.page?.startsWith(bookUrl(name))) {
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

function startTouch(event) {
  touchStart = event.touches.length === 1 ? { x: event.touches[0].clientX, y: event.touches[0].clientY } : null;
}

// A pinch-zoomed page leaves a sideways drag to pan it.
function turnOnSwipe(event) {
  const touch = event.changedTouches[0];
  if (touchStart && event.touches.length === 0 && !(visualViewport?.scale > 1.01)) {
    const offset = swipeStep(touchStart, { x: touch.clientX, y: touch.clientY });
    if (offset) {
      step(offset);
    }
  }
  touchStart = null;
}

// The presenter hides the pointer and the exit button until the pointer moves or a finger touches.
function showControls() {
  present.classList.add('controls');
  clearTimeout(controlsTimer);
  controlsTimer = setTimeout(() => present.classList.remove('controls'), CONTROLS_MS);
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
  // iPhones have no fullscreen for a page, so the exit button is the way out there. Where fullscreen works, a phone
  // held upright turns the slide sideways.
  if (present.requestFullscreen) {
    present.requestFullscreen().then(() => screen.orientation?.lock?.('landscape')).catch(() => {});
  }
  showControls();
  renderPresent();
}

function stopPresenting() {
  state.presenting = false;
  present.hidden = true;
  if (document.fullscreenElement) {
    document.exitFullscreen().catch(() => {});
  }
}

present.addEventListener('click', (event) => {
  const frame = present.querySelector('.present-frame');
  if (frame && !event.target.closest('button')) {
    step(tapStep(event.clientX, frame.getBoundingClientRect()));
  }
});
present.addEventListener('wheel', turnOnWheel, { passive: true });
present.addEventListener('pointermove', showControls);
present.addEventListener('pointerdown', showControls);
document.getElementById('view').addEventListener('wheel', turnOnWheel, { passive: true });
// On a narrow page the sidebar lies over the slide, so a tap beside it closes it rather than turning the slide.
document.getElementById('view').addEventListener('click', (event) => {
  if (NARROW.matches && !layout.classList.contains('nav-hidden') && !event.target.closest('#nav-show')) {
    event.stopPropagation();
    setNav(false);
  }
}, { capture: true });
for (const target of [present, slideArea]) {
  target.addEventListener('touchstart', startTouch, { passive: true });
  target.addEventListener('touchend', turnOnSwipe, { passive: true });
}
document.getElementById('present-exit').addEventListener('click', stopPresenting);
document.getElementById('previous').addEventListener('click', () => step(-1));
document.getElementById('next').addEventListener('click', () => step(1));
document.getElementById('present-button').addEventListener('click', startPresenting);
document.getElementById('nav-hide').addEventListener('click', () => setNav(false));
document.getElementById('nav-show').addEventListener('click', () => setNav(true));

document.addEventListener('fullscreenchange', () => {
  if (!document.fullscreenElement && state.presenting) {
    stopPresenting();
  }
});

// Holding Ctrl underlines the numbers a click traces, as in the preview.
for (const type of ['keydown', 'keyup']) {
  document.addEventListener(type, (event) => {
    document.body.classList.toggle('ctrl-held', event.ctrlKey || event.metaKey);
  });
}
window.addEventListener('blur', () => document.body.classList.remove('ctrl-held'));

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
  document.getElementById('trace-hint-close').addEventListener('click', endHint);
  slideArea.addEventListener('pointerdown', startPress);
  slideArea.addEventListener('pointermove', movePress);
  slideArea.addEventListener('pointerup', endPress);
  slideArea.addEventListener('pointercancel', endPress);
  // Android's long press would open the browser's menu over the book.
  slideArea.addEventListener('contextmenu', (event) => {
    if (press?.pointer === 'touch' && traceAt(event.target)) {
      event.preventDefault();
    }
  });
}

state.current = indexOf(decodeURIComponent(location.hash.slice(1)));
renderSidebar();
render();
// Text is fitted as it is drawn, so fonts that arrive afterwards need the slides fitted again.
useFonts(deck.fonts).then(() => document.fonts.ready).then(render);