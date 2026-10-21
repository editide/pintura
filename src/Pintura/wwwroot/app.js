'use strict';

// The preview: a sidebar of thumbnails beside one slide at fit, a grid of every slide, or the theme in theme.js, the
// inspector in inspector.js, and present mode, all redrawn whenever the server announces a change.
// Mouse edits on the slide are in edit.js, and comparing against a git revision in compare.js.

const THUMBNAIL_WIDTH = 182;
// The sidebar's padding, number, and gap beside each thumbnail.
const SIDEBAR_CHROME = 50;
const SIDEBAR_WIDTH = [160, 480];
const STRIP_HEIGHT = 140;
const STRIP_HEIGHTS = [100, 320];
// The strip's padding, scrollbar, and border above and below each thumbnail.
const STRIP_CHROME = 48;
const GRID_WIDTH = 300;
const SETTINGS = 'pintura.view';
const INSPECTOR_WIDTH = [300, 640];

const saved = JSON.parse(localStorage.getItem(SETTINGS) || '{}');

const state = {
  deck: null,
  // The cell the book last opened at, which popping the book out opens in a tab.
  traced: null,
  // What the theme view draws, fetched beside the deck.
  themeView: null,
  current: null,
  // The layout whose specimen the slide view shows for its frames, areas, and elements to be moved.
  layout: null,
  view: ['grid', 'theme'].includes(saved.view) ? saved.view : 'slide',
  guides: Boolean(saved.guides),
  inspector: saved.inspector ?? false,
  changes: Boolean(saved.changes),
  changedOnly: Boolean(saved.changedOnly),
  comments: saved.comments ?? true,
  nav: saved.nav ?? true,
  // Whether the slides run along the bottom rather than down the side.
  strip: Boolean(saved.strip),
  theme: saved.theme === 'dark' ? 'dark' : 'light',
  inspectorWidth: saved.inspectorWidth || null,
  sidebarWidth: saved.sidebarWidth || null,
  stripHeight: saved.stripHeight || null,
  issuesHidden: Boolean(saved.issuesHidden),
  // Which inspector sections are folded, by kind and section, when it differs from the default.
  folds: saved.folds || {},
  // The revision compared against, as drawn, and what changed since; its error when there's nothing to compare.
  compare: null,
  before: false,
  presenting: false,
  // The slide's zoom over its fit, kept between slides but not between visits.
  zoom: 1,
  gridZoom: saved.gridZoom || 1,
  // The server addresses an item by its place in the slide's drawing order; its id and owner find it again after an
  // edit reorders the slide.
  selected: null,
  // Items picked together, each as selected names it, to move or delete at once; selected is then null.
  group: null,
  detail: null,
  findings: null,
  // Whether the page's event stream is open; null until it first opens or fails.
  live: null
};

function save() {
  const { view, guides, inspector, changes, changedOnly, comments, nav, strip, theme, inspectorWidth, sidebarWidth,
    stripHeight, issuesHidden, folds, gridZoom } = state;
  localStorage.setItem(SETTINGS, JSON.stringify({ view, guides, inspector, changes, changedOnly, comments, nav, strip,
    theme, inspectorWidth, sidebarWidth, stripHeight, issuesHidden, folds, gridZoom }));
}

// A thumbnail fills the sidebar's width, or the strip's height.
function thumbnailWidth(size) {
  if (state.strip) {
    return Math.round(((state.stripHeight || STRIP_HEIGHT) - STRIP_CHROME) * size[0] / size[1]);
  }
  return state.sidebarWidth ? state.sidebarWidth - SIDEBAR_CHROME : THUMBNAIL_WIDTH;
}

function currentSlide() {
  const layout = state.layout && state.themeView?.layouts.find((candidate) => candidate.id === state.layout);
  if (layout) {
    return layout.stage;
  }
  const slides = state.deck.slides;
  return slides.find((slide) => slide.id === state.current) || removedSlide(state.current) || slides[0];
}

// Each slide's thumbnail with the payload it drew, so a reload redraws only the slides that changed.
const thumbnails = new Map();

function renderSidebar() {
  const deck = state.deck;
  const title = document.getElementById('deck-title');
  title.textContent = deck.name;
  title.title = deck.name;
  const container = document.getElementById('thumbnails');
  container.replaceChildren();
  // The strip's section names are as wide as a thumbnail.
  container.parentElement.style.setProperty('--thumbnail-width', `${thumbnailWidth(deck.size)}px`);
  const bySection = new Map(deck.slides.map((slide) => [slide.id, slide]));
  const filtering = filteringChanges();
  for (const section of deck.sections) {
    if (section.name) {
      const name = element('div', 'section-name', container);
      element('span', null, name).textContent = section.name;
      name.dataset.section = section.name;
      name.hidden = filtering && !section.slides.some(slideDiff);
    }
    for (const id of section.slides) {
      const slide = bySection.get(id);
      const width = thumbnailWidth(deck.size);
      const drawn = JSON.stringify([deck.size, width, slide]);
      let thumbnail = thumbnails.get(id);
      if (!thumbnail || thumbnail.drawn !== drawn) {
        const button = element('button', 'thumbnail');
        button.type = 'button';
        button.dataset.slide = id;
        element('span', 'number', button);
        mountStage(element('div', 'preview', button), slide, deck.size, width);
        button.addEventListener('click', () => pickSlide(id));
        thumbnail = { drawn, button };
        thumbnails.set(id, thumbnail);
      }
      const { button } = thumbnail;
      container.append(button);
      button.querySelector('.number').textContent = slide.number;
      button.title = slide.title || id;
      delete button.dataset.change;
      markThumbnail(button, id);
      markComments(button.querySelector('.preview'), id);
      button.hidden = filtering && !slideDiff(id);
    }
  }
  for (const id of thumbnails.keys()) {
    if (!bySection.has(id)) {
      thumbnails.delete(id);
    }
  }
  renderRemoved();
  renderStrayComments();
  renderCommentSummary();
}

function renderToolbar() {
  const pressed = {
    slide: state.view === 'slide' && !state.layout,
    grid: state.view === 'grid',
    theme: state.view === 'theme' || Boolean(state.layout),
    guides: state.guides,
    inspector: state.inspector,
    before: state.before,
    changes: state.changes,
    present: state.presenting
  };
  for (const button of document.querySelectorAll('.toolbar [data-action]')) {
    button.setAttribute('aria-pressed', String(pressed[button.dataset.action]));
  }
  // A version that failed to compare still shows its picker, to choose another.
  document.getElementById('compare').hidden = !comparing() && compareBase() === 'HEAD';
  const since = namedBase() || 'last save';
  document.getElementById('versions-label').textContent = `Since ${since}`;
  renderStatus();
  for (const button of document.querySelectorAll('.toolbar [data-compare]')) {
    button.hidden = !changesToShow();
    button.title = button.dataset.compare.replace(' since the revision', sinceBase())
      .replace('the revision', namedBase() || 'before the changes');
  }
  const errors = (state.findings?.list || []).filter((finding) => finding.level === 'error').length;
  const badge = document.getElementById('issue-badge');
  badge.hidden = errors === 0;
  badge.textContent = errors;
  badge.title = `${errors} ${errors === 1 ? 'error' : 'errors'} on this slide`;
  document.getElementById('nav-show').hidden = state.nav;
  const strip = document.getElementById('strip-toggle');
  strip.title = state.strip ? 'Show the slides beside the slide (|)' : 'Show the slides below the slide (|)';
  strip.setAttribute('aria-label', strip.title);
}

// Everything but the sidebar, which only a new deck changes.
function renderMain() {
  renderToolbar();
  document.getElementById('slide-view').hidden = state.view !== 'slide';
  document.getElementById('grid').hidden = state.view !== 'grid';
  document.getElementById('theme-view').hidden = state.view !== 'theme';
  document.getElementById('inspector').hidden = !state.inspector;
  const layout = document.getElementById('layout');
  layout.classList.toggle('inspecting', state.inspector);
  layout.classList.toggle('nav-hidden', !state.nav);
  layout.classList.toggle('strip', state.strip);
  if (state.view === 'slide') {
    renderSlide();
  } else if (state.view === 'grid') {
    renderGrid();
  } else {
    renderTheme(state.themeView);
  }
  renderCurrent();
  renderPresent();
  renderInspector();
}

function renderSlide() {
  const deck = state.deck;
  const slide = currentSlide();
  if (!slide) {
    return;
  }
  const view = document.getElementById('slide-view');
  view.classList.toggle('zoomed', state.zoom > 1);
  // Below the strip's slide, the caption shares a row with the status pill floating above the strip.
  const height = view.clientHeight - (state.strip && state.nav ? 96 : 48);
  const available = state.zoom * Math.max(200,
    Math.min(view.clientWidth - 48, height * deck.size[0] / deck.size[1]));
  const shown = shownSlide(slide);
  const frame = document.getElementById('slide-frame');
  frame.classList.toggle('reviewing', reviewing(slide));
  const stage = shown ? mountStage(frame, shown, deck.size, available) : mountAbsent(frame, deck.size, available);
  if (state.guides && shown && !shown.error && !state.layout) {
    renderGuides(shown, stage);
  }
  if (state.changes && comparing()) {
    renderChanges(slide, stage);
  }
  for (const node of stage.querySelectorAll('.item')) {
    const edit = shown.items[node.dataset.index].edit;
    node.classList.toggle('fixed', !edit || Boolean(edit.locked || !(edit.frame || edit.from)));
    node.classList.toggle('rewordable', Boolean(edit?.text));
  }
  markSelection();
  if (state.layout) {
    layoutCaption();
    return;
  }
  if (shown && !shown.error && !reviewing(slide)) {
    renderComments(shown, frame, deck.size);
  }
  const drawn = shown || slide;
  const revision = namedBase() ? `as of ${namedBase()}` : 'before the changes';
  const caption = [`Slide ${drawn.number} of ${deck.slides.length}`, reviewing(slide) && revision,
    state.zoom !== 1 && `${Math.round(state.zoom * 100)}%`];
  document.getElementById('slide-caption').textContent = caption.filter(Boolean).join(' · ');
}

// The layout's stage names the layout, how many slides it moves, and the way back to the theme.
function layoutCaption() {
  const layout = state.themeView.layouts.find((candidate) => candidate.id === state.layout);
  const caption = document.getElementById('slide-caption');
  const name = layout.name || words(layout.id);
  caption.replaceChildren(`${name} layout · used by ${countLabel(layout.slides.length, 'slide')} · `);
  const back = element('button', 'link', caption);
  back.type = 'button';
  element('span', 'link-arrow', back).textContent = '←';
  back.append('Back to the theme');
  back.addEventListener('click', leaveLayout);
}

function renderGuides(slide, stage) {
  const layer = element('div', 'guides', stage);
  for (const guide of slide.guides || []) {
    const node = element('div', `guide ${guide.kind}${guide.used ? ' used' : ''}`, layer);
    place(node, guide.rect);
    element('span', 'guide-label', node).textContent = guide.name;
  }
}

function renderGrid() {
  const deck = state.deck;
  const grid = document.getElementById('grid');
  grid.replaceChildren();
  const byId = new Map(deck.slides.map((slide) => [slide.id, slide]));
  const filtering = filteringChanges();
  for (const section of deck.sections) {
    const ids = filtering ? section.slides.filter(slideDiff) : section.slides;
    if (ids.length === 0) {
      continue;
    }
    if (section.name) {
      const head = element('div', 'grid-section', grid);
      head.dataset.section = section.name;
      element('span', null, head).textContent = section.name;
      element('span', 'grid-section-count', head).textContent =
        `${ids.length} ${filtering ? 'changed ' : ''}slide${ids.length === 1 ? '' : 's'}`;
    }
    const row = element('div', 'grid-slides', grid);
    for (const id of ids) {
      const slide = byId.get(id);
      const cell = element('button', 'grid-cell', row);
      cell.type = 'button';
      cell.dataset.slide = id;
      cell.style.width = `${GRID_WIDTH * state.gridZoom}px`;
      const shown = shownSlide(slide);
      const frame = element('div', 'grid-frame', cell);
      if (shown) {
        mountStage(frame, shown, deck.size, GRID_WIDTH * state.gridZoom);
      } else {
        mountAbsent(frame, deck.size, GRID_WIDTH * state.gridZoom);
      }
      markThumbnail(cell, id);
      markComments(frame, id);
      element('div', 'grid-caption', cell).textContent = `${slide.number} · ${slide.title || words(slide.id)}`;
      cell.addEventListener('click', () => {
        select(id);
        setView('slide');
      });
    }
  }
}

// Marks the current slide in the sidebar and the grid, without redrawing either.
function renderCurrent() {
  const slide = currentSlide();
  if (!slide) {
    return;
  }
  for (const button of document.querySelectorAll('.thumbnail, .grid-cell')) {
    const current = button.dataset.slide === slide.id;
    button.classList.toggle('current', current);
    if (current && !button.closest('[hidden]')) {
      button.scrollIntoView({ block: 'nearest' });
    }
  }
  document.title = `editide | ${state.deck.name}`;
}

function select(id) {
  flushNudge();
  if (state.layout) {
    state.layout = null;
    state.selected = null;
    state.group = null;
    state.detail = null;
  }
  if (id !== state.current) {
    state.selected = null;
    state.group = null;
    state.detail = null;
  }
  state.current = id;
  history.replaceState(null, '', `#${encodeURIComponent(id)}`);
  unflipUnchanged();
  if (state.view === 'slide') {
    renderSlide();
  }
  renderToolbar();
  renderCurrent();
  renderPresent();
  renderInspector();
}

// A slide picked in the sidebar shows in place of the theme, which has no slide to show it on.
function pickSlide(id) {
  select(id);
  if (state.view === 'theme') {
    setView('slide');
  }
}

// Filtering to the changed slides narrows stepping through them too, but presenting always shows every slide.
function step(offset) {
  if (state.layout) {
    stepLayout(offset);
    return;
  }
  const slides = state.deck.slides;
  if (filteringChanges() && !state.presenting) {
    const changed = new Set(state.compare.diff.slides.map((slide) => slide.id));
    select(changedStep(slides.map((slide) => slide.id), changed, currentSlide().id, offset));
    return;
  }
  const index = slides.indexOf(currentSlide());
  const next = slides[Math.min(Math.max(index + offset, 0), slides.length - 1)];
  select(next.id);
}

function installWheelNavigation(container) {
  container.addEventListener('wheel', (event) => {
    const offset = wheelStep(event, state, editing, container);
    if (offset) {
      event.preventDefault();
      step(offset);
    }
  }, { passive: false });
}

// #region Zoom

// The steps the current view zooms through.
function zoomSteps() {
  return state.view === 'grid' ? GRID_ZOOM_STEPS : ZOOM_STEPS;
}

function currentZoom() {
  return state.view === 'grid' ? state.gridZoom : state.zoom;
}

function zoomable() {
  return state.deck && ['slide', 'grid'].includes(state.view) && !state.presenting;
}

// Zooms the slide, or the grid's thumbnails, about a point on screen, the middle of the view unless given.
function zoomTo(zoom, pointer) {
  if (!zoomable() || editing.text || editing.gesture || zoom === currentZoom()) {
    return;
  }
  const grid = state.view === 'grid';
  const view = document.getElementById(grid ? 'grid' : 'slide-view');
  const box = view.getBoundingClientRect();
  const at = pointer ?? { x: box.left + box.width / 2, y: box.top + box.height / 2 };
  // Thumbnails rewrap as they grow, so the grid keeps the one nearest the point under it.
  const frame = grid ? nearestFrame(view, at) : document.getElementById('slide-frame');
  const id = frame?.closest('.grid-cell')?.dataset.slide;
  const before = frame?.getBoundingClientRect();
  if (grid) {
    state.gridZoom = zoom;
    save();
    renderGrid();
  } else {
    state.zoom = zoom;
    renderSlide();
  }
  const after = grid ? view.querySelector(`.grid-cell[data-slide="${CSS.escape(id ?? '')}"] .grid-frame`) : frame;
  if (!before || !after) {
    return;
  }
  const scroll = anchoredScroll({ left: view.scrollLeft, top: view.scrollTop }, at, before,
    after.getBoundingClientRect());
  view.scrollLeft = scroll.left;
  view.scrollTop = scroll.top;
}

function nearestFrame(view, point) {
  let nearest = null;
  let distance = Infinity;
  for (const frame of view.querySelectorAll('.grid-frame')) {
    const box = frame.getBoundingClientRect();
    const dx = Math.max(box.left - point.x, 0, point.x - box.right);
    const dy = Math.max(box.top - point.y, 0, point.y - box.bottom);
    if (dx * dx + dy * dy < distance) {
      distance = dx * dx + dy * dy;
      nearest = frame;
    }
  }
  return nearest;
}

// Ctrl and the wheel, or a pinch, zoom the slide or grid rather than the page; frames redraw at most once each.
function installZoom(view) {
  let pending = null;
  view.addEventListener('wheel', (event) => {
    if (!(event.ctrlKey || event.metaKey) || !zoomable()) {
      return;
    }
    event.preventDefault();
    if (!pending) {
      requestAnimationFrame(() => {
        zoomTo(pending.zoom, pending.pointer);
        pending = null;
      });
    }
    pending = { zoom: wheelZoom(pending?.zoom ?? currentZoom(), event, zoomSteps()),
      pointer: { x: event.clientX, y: event.clientY } };
  }, { passive: false });
}

// Ctrl with + or - steps the zoom, and with 0 fits it again, leaving the page's zoom to the browser's menu.
function zoomKey(event) {
  const directions = { '=': 1, '+': 1, '-': -1, '0': 0 };
  if (!(event.ctrlKey || event.metaKey) || event.altKey || !(event.key in directions) || !zoomable()) {
    return false;
  }
  const direction = directions[event.key];
  zoomTo(direction ? zoomStep(currentZoom(), direction, zoomSteps()) : 1);
  return true;
}

// #endregion

function setView(view) {
  flushNudge();
  if (state.layout) {
    state.layout = null;
    state.selected = null;
    state.group = null;
    state.detail = null;
  }
  state.view = view;
  save();
  unflipUnchanged();
  renderMain();
}

// The view a layout link left, which back returns to.
let layoutReturn = null;

function showLayout(id, push = true) {
  if (push) {
    history.pushState({ ...history.state, layout: id }, '');
  }
  layoutReturn = state.view;
  setView('theme');
  const card = document.querySelector(`#theme-view .layout-card[data-layout="${CSS.escape(id)}"]`);
  card?.scrollIntoView({ block: 'center' });
}

// A layout's specimen on the slide view, where dragging its title, placeholders, areas, and elements writes the
// theme's and layout's files.
function editLayout(id) {
  setView('slide');
  state.layout = id;
  renderMain();
}

// The wheel and arrow keys turn through the theme's layouts as they would through slides.
function stepLayout(offset) {
  const layouts = state.themeView.layouts;
  const index = layouts.findIndex((layout) => layout.id === state.layout);
  const next = layouts[Math.min(Math.max(index + offset, 0), layouts.length - 1)];
  if (next.id !== state.layout) {
    editLayout(next.id);
  }
}

function leaveLayout() {
  const id = state.layout;
  setView('theme');
  const card = document.querySelector(`#theme-view .layout-card[data-layout="${CSS.escape(id)}"]`);
  card?.scrollIntoView({ block: 'center' });
}

function toggle(setting) {
  state[setting] = !state[setting];
  save();
  renderMain();
}

function toggleStrip() {
  state.strip = !state.strip;
  state.nav = true;
  save();
  renderSidebar();
  renderMain();
}

// Flipping isn't saved: a reload should never open on the revision, where edits don't write.
function flipBefore() {
  state.before = !state.before;
  renderMain();
}

// A slide with no changes has no revision worth flipping to, so arriving on one flips back.
function unflipUnchanged() {
  if (!changesToShow()) {
    state.before = false;
  }
}

// #region Present mode

function startPresenting() {
  state.presenting = true;
  const present = document.getElementById('present');
  present.hidden = false;
  if (present.requestFullscreen) {
    present.requestFullscreen().catch(() => {});
  }
  renderToolbar();
  renderPresent();
}

function stopPresenting() {
  state.presenting = false;
  document.getElementById('present').hidden = true;
  if (document.fullscreenElement) {
    document.exitFullscreen().catch(() => {});
  }
  renderMain();
}

function renderPresent() {
  if (!state.presenting) {
    return;
  }
  const size = state.deck.size;
  const present = document.getElementById('present');
  const frame = element('div', 'present-frame');
  present.replaceChildren(frame);
  mountStage(frame, currentSlide(), size, Math.min(innerWidth, innerHeight * size[0] / size[1]));
}

// #endregion

// #region Inspector

// Selecting leaves the inspector as it is: opening it would shrink the slide under the pointer mid double click.
function selectItem(selection) {
  flushNudge();
  state.selected = selection;
  state.group = null;
  state.detail = null;
  markSelection();
  renderInspector();
  loadDetail();
}

// Several items, by index, picked on the current slide; fewer than two select the one or none.
function selectGroup(indexes) {
  const slide = currentSlide();
  const items = [...new Set(indexes)].sort((one, other) => one - other)
    .map((index) => ({ slide: slide.id, index, id: slide.items[index].id, owner: slide.items[index].owner }));
  if (items.length < 2) {
    selectItem(items[0] ? { ...items[0], paragraph: null, cell: null } : null);
    return;
  }
  flushNudge();
  state.selected = null;
  state.group = items;
  state.detail = null;
  markSelection();
  renderInspector();
}

// The revision's drawing numbers its items differently, and nothing on it is editable.
function selectionShown(selection) {
  return state.view === 'slide' && selection.slide === currentSlide().id && !reviewing();
}

function selectedNode() {
  const selection = state.selected;
  if (!selection || !selectionShown(selection)) {
    return null;
  }
  return document.querySelector(`#slide-frame .item[data-index="${selection.index}"]`);
}

// The indexes of the selected item or group, where the slide view shows them.
function selectedIndexes() {
  if (state.group) {
    return selectionShown(state.group[0]) ? state.group.map((selection) => selection.index) : [];
  }
  return selectedNode() ? [state.selected.index] : [];
}

function markSelection(indexes = selectedIndexes()) {
  for (const node of document.querySelectorAll('#slide-frame .item.selected')) {
    node.classList.remove('selected');
  }
  for (const index of indexes) {
    document.querySelector(`#slide-frame .item[data-index="${index}"]`)?.classList.add('selected');
  }
  drawSelection();
}

// After a reload, the selected item is the one with the same id and owner, wherever the slide now draws it.
function reselected(selection) {
  const slide = state.deck.slides.find((candidate) => candidate.id === selection.slide);
  const index = slide && slide.items
    ? slide.items.findIndex((item) => item.id === selection.id && item.owner === selection.owner)
    : -1;
  return index < 0 ? null : { ...selection, index };
}

function reselect() {
  if (state.selected) {
    state.selected = reselected(state.selected);
  }
  if (state.group) {
    const group = state.group.map(reselected).filter(Boolean);
    state.group = group.length > 1 ? group : null;
    state.selected = group.length === 1 ? { ...group[0], paragraph: null, cell: null } : null;
  }
}

async function loadDetail() {
  const selection = state.selected;
  if (!selection) {
    return;
  }
  const query = `${state.layout ? 'layout' : 'slide'}=${encodeURIComponent(selection.slide)}&index=${selection.index}`;
  const response = await fetch(`api/element?${query}`, { cache: 'no-store' });
  const detail = response.ok ? await response.json() : null;
  if (state.selected === selection) {
    state.detail = detail;
    renderInspector();
  }
}

// #endregion

// The cell in Libreta's live page, slid in over the preview at once while Libreta answers. Libreta has its open pages
// select the cell, so a frame already on the book's server follows it without reloading.
async function trace(target) {
  if (bookFrame.dataset.book !== target.book) {
    showInBook('about:blank');
    bookFrame.dataset.book = target.book;
    delete bookFrame.dataset.origin;
  }
  state.traced = target;
  showBook(currentSlide());
  const response = await fetch('api/trace', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(target)
  });
  if (!response.ok) {
    closeBook();
    notice((await response.json()).error);
    return;
  }
  const page = await response.json();
  const origin = new URL(page.url).origin;
  if (page.pages === 0 || bookFrame.dataset.origin !== origin) {
    showInBook(page.url);
    bookFrame.dataset.origin = origin;
  }
}

// The cell in a Libreta tab named for the book, so each one finds the same tab. Only a click may bring a tab forward,
// and only one this page opened, so the tab is claimed before anything is awaited; an open page selects the cell
// itself, and a new or stale tab is sent to it.
async function traceInTab(target) {
  const tab = window.open('', `libreta:${target.book}`);
  tab?.focus();
  const fresh = isBlank(tab);
  if (fresh) {
    tab.document.title = 'Opening Libreta…';
  }
  const response = await fetch('api/trace', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(target)
  });
  if (!response.ok) {
    if (fresh) {
      tab.close();
    }
    notice((await response.json()).error);
    return;
  }
  const page = await response.json();
  if (!tab) {
    window.open(page.url, `libreta:${target.book}`);
  } else if (fresh || page.pages === 0) {
    tab.location = page.url;
  }
}

// A tab this page just opened is blank and readable; one already showing Libreta is another origin's.
function isBlank(tab) {
  try {
    return tab?.location.href === 'about:blank';
  } catch {
    return false;
  }
}

function showError(message) {
  const banner = document.getElementById('error');
  banner.hidden = !message;
  banner.textContent = message || '';
}

// One load runs at a time: a save during it loads again once it ends, so an older answer arriving late never
// draws over a newer one.
const loading = { running: null, again: false };

function load() {
  if (editingActive()) {
    editing.reloadPending = true;
    return Promise.resolve();
  }
  if (loading.running) {
    loading.again = true;
    return loading.running;
  }
  loading.running = (async () => {
    try {
      do {
        loading.again = false;
        await loadOnce();
      } while (loading.again);
    } finally {
      loading.running = null;
    }
  })();
  return loading.running;
}

async function loadOnce() {
  const [response, compare, theme] = await Promise.all([fetch('api/presentation', { cache: 'no-store' }),
    fetchCompare(), fetch('api/theme', { cache: 'no-store' })]);
  const deck = await response.json();
  await useFonts(deck.fonts);
  // An edit begun while this waited would have its slide redrawn out from under it.
  if (editingActive()) {
    editing.reloadPending = true;
    return;
  }
  if (theme.ok) {
    state.themeView = await theme.json();
  }
  state.compare = compare;
  if (!comparing()) {
    state.before = false;
  }
  showError(deck.error);
  // A presentation that failed to load keeps showing its last good slides beneath the error.
  if (deck.slides.length === 0 && state.deck) {
    return;
  }
  state.deck = deck;
  if (!state.current) {
    state.current = decodeURIComponent(location.hash.slice(1)) || null;
  }
  reselect();
  renderSidebar();
  renderMain();
  loadDetail();
}

document.getElementById('slide-view').addEventListener('click', (event) => {
  // A drag ends in a click, which mustn't select what the pointer was released over.
  if (editing.suppressClick) {
    editing.suppressClick = false;
    return;
  }
  const traced = event.target.closest('[data-trace]');
  if (traced && (event.ctrlKey || event.metaKey)) {
    trace(JSON.parse(traced.dataset.trace));
    return;
  }
  if (reviewing()) {
    return;
  }
  const node = event.target.closest('.item');
  if (event.shiftKey || event.ctrlKey || event.metaKey) {
    if (node) {
      const index = Number(node.dataset.index);
      const indexes = selectedIndexes();
      selectGroup(indexes.includes(index) ? indexes.filter((other) => other !== index) : [...indexes, index]);
    }
    return;
  }
  if (!node) {
    selectItem(null);
    return;
  }
  // Clicking the selected item again rewords its text, as a double click does on any item.
  const again = selectedNode() === node;
  const cell = event.target.closest('td');
  const paragraph = cell ? null : event.target.closest('.paragraph');
  const point = event.target.closest('[data-point]');
  selectItem({
    slide: currentSlide().id,
    index: Number(node.dataset.index),
    id: node.dataset.id,
    owner: node.dataset.owner,
    paragraph: paragraph ? Number(paragraph.dataset.paragraph) : null,
    cell: cell ? { row: Number(cell.dataset.row), column: Number(cell.dataset.column) } : null,
    point: point ? { series: Number(point.dataset.series), point: Number(point.dataset.point) } : null
  });
  if (again) {
    startText(event);
  }
});

// Tab steps through the slide's items in drawing order, wrapping at either end, wherever focus was left by a click; only
// the inspector's controls keep Tab for moving focus.
function tabKey(event) {
  const items = currentSlide()?.items ?? [];
  if (event.key !== 'Tab' || state.view !== 'slide' || state.presenting || reviewing() || items.length === 0
    || event.target.closest('#inspector')) {
    return false;
  }
  // A button left focused would otherwise answer the Space or Enter meant for the slide.
  document.activeElement?.blur();
  const current = selectedNode() ? state.selected.index : event.shiftKey ? 0 : -1;
  const index = (current + (event.shiftKey ? -1 : 1) + items.length) % items.length;
  pickItem(currentSlide(), index);
  return true;
}

// A selected chart shows the value under the pointer that no label gives.
const chartTip = element('div', 'chart-tip', document.body);
chartTip.hidden = true;

document.getElementById('slide-view').addEventListener('pointermove', (event) => {
  const mark = event.target.closest?.('#slide-frame .item.chart.selected [data-hover]');
  if (!mark) {
    chartTip.hidden = true;
    return;
  }
  const { value, of } = JSON.parse(mark.dataset.hover);
  chartTip.replaceChildren();
  element('strong', null, chartTip).textContent = value;
  if (of) {
    element('span', null, chartTip).textContent = of;
  }
  chartTip.hidden = false;
  chartTip.style.left = `${event.clientX + 12}px`;
  chartTip.style.top = `${event.clientY + 14}px`;
});

document.getElementById('slide-view').addEventListener('pointerleave', () => {
  chartTip.hidden = true;
});

function markCtrl(event) {
  document.body.classList.toggle('ctrl-held', event.ctrlKey || event.metaKey);
}

document.addEventListener('keydown', markCtrl);
document.addEventListener('keyup', markCtrl);
// Ctrl released in another window sends no keyup here.
window.addEventListener('blur', () => document.body.classList.remove('ctrl-held'));

document.getElementById('book-pop-out').addEventListener('click', () => {
  closeBook();
  traceInTab(state.traced);
});

installEditing(document.getElementById('slide-frame'), document.getElementById('slide-view'));
installReordering(document.getElementById('thumbnails'), {
  draggable: '.thumbnail',
  picture: '.preview',
  scroller: document.querySelector('.sidebar-scroll'),
  slot: (container, x, y) => (state.strip ? stripSlot : sidebarSlot)(container, x, y)
});
installReordering(document.getElementById('grid'), {
  draggable: '.grid-cell',
  picture: '.grid-frame',
  scroller: document.getElementById('grid'),
  slot: gridSlot
});

document.querySelector('.toolbar').addEventListener('click', (event) => {
  const button = event.target.closest('[data-action]');
  if (!button || !state.deck) {
    return;
  }
  const actions = {
    slide: () => setView('slide'),
    grid: () => setView('grid'),
    theme: () => setView('theme'),
    inspector: () => toggle('inspector'),
    before: flipBefore,
    changes: () => toggle('changes'),
    present: startPresenting
  };
  actions[button.dataset.action]();
});

document.getElementById('present').addEventListener('click', () => step(1));
installWheelNavigation(document.getElementById('slide-view'));
installZoom(document.getElementById('slide-view'));
installZoom(document.getElementById('grid'));
installWheelNavigation(document.getElementById('present'));
installComments(document.getElementById('slide-view'));

document.addEventListener('fullscreenchange', () => {
  if (!document.fullscreenElement && state.presenting) {
    stopPresenting();
  }
});

document.addEventListener('keydown', (event) => {
  if (!state.deck || event.target.closest('input, textarea, [contenteditable="true"]')) {
    return;
  }
  // The slides lie hidden under an open book, so only closing it applies.
  if (bookShown()) {
    if (event.key === 'Escape') {
      closeBook();
      event.preventDefault();
    }
    return;
  }
  if (undoKey(event) || zoomKey(event)) {
    event.preventDefault();
    return;
  }
  if (event.ctrlKey || event.metaKey || event.altKey || document.getElementById('shortcuts').open) {
    return;
  }
  // A focused button already answers Space and Enter.
  if (event.target.closest('button') && (event.key === ' ' || event.key === 'Enter')) {
    return;
  }
  const offsets = { ArrowRight: 1, ArrowDown: 1, PageDown: 1, ' ': 1, ArrowLeft: -1, ArrowUp: -1, PageUp: -1 };
  const commands = {
    Home: () => (state.layout ? stepLayout(-Infinity) : select(state.deck.slides[0].id)),
    End: () => (state.layout ? stepLayout(Infinity) : select(state.deck.slides[state.deck.slides.length - 1].id)),
    Enter: () => state.view === 'grid' && setView('slide'),
    Escape: () => (state.presenting ? stopPresenting() : selectItem(null)),
    s: () => setView('slide'),
    g: () => setView('grid'),
    t: () => setView('theme'),
    o: () => toggle('guides'),
    i: () => toggle('inspector'),
    m: () => toggle('comments'),
    n: () => stepComments(1),
    N: () => stepComments(-1),
    b: () => changesToShow() && flipBefore(),
    c: () => changesToShow() && toggle('changes'),
    d: () => !document.getElementById('status').disabled && toggleChangedOnly(),
    p: () => !state.presenting && startPresenting(),
    F5: () => !state.presenting && startPresenting(),
    '\\': () => toggle('nav'),
    '|': toggleStrip,
    '?': () => document.getElementById('shortcuts').showModal()
  };
  if (tabKey(event) || nudgeKey(event) || deleteSlideKey(event) || deleteKey(event)) {
    event.preventDefault();
    return;
  }
  // The theme view scrolls with the keys that would otherwise turn the slide behind it.
  if (event.key in offsets && state.view === 'theme') {
    return;
  }
  if (event.key in offsets) {
    step(offsets[event.key]);
  } else if (event.key in commands) {
    commands[event.key]();
  } else {
    return;
  }
  event.preventDefault();
});

window.addEventListener('popstate', () => {
  const layout = history.state?.layout;
  if (layout && state.view !== 'theme') {
    showLayout(layout, false);
  } else if (!layout && layoutReturn) {
    setView(layoutReturn);
    layoutReturn = null;
  }
});

window.addEventListener('hashchange', () => {
  const id = decodeURIComponent(location.hash.slice(1));
  if (state.deck && id && id !== state.current) {
    select(id);
  }
});

window.addEventListener('resize', () => {
  if (!state.deck) {
    return;
  }
  if (state.view === 'slide') {
    renderSlide();
  }
  renderPresent();
});

// #region Export

/** The file name the server gives an export, from its Content-Disposition header. */
function downloadName(disposition, format) {
  const encoded = disposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  if (encoded) {
    return decodeURIComponent(encoded);
  }
  return disposition?.match(/filename="?([^";]+)"?/i)?.[1] ?? `presentation.${format}`;
}

/** What the export couldn't carry over, from its Pintura-Findings header: a URL-encoded JSON array of strings. */
function exportFindings(header) {
  if (!header) {
    return [];
  }
  try {
    const findings = JSON.parse(decodeURIComponent(header));
    return Array.isArray(findings) ? findings.map(String) : [];
  } catch {
    return [`the export's findings couldn't be read: ${header}`];
  }
}

// Shown until dismissed, rather than as a passing notice, so they are read before the file is handed on.
function showExportFindings(name, findings) {
  const panel = document.getElementById('export-findings');
  panel.hidden = findings.length === 0;
  const count = findings.length === 1 ? 'one thing' : `${findings.length} things`;
  panel.querySelector('p').textContent = `${name} is downloaded, with ${count} it couldn't carry over:`;
  panel.querySelector('ul').replaceChildren(...findings.map((finding) => {
    const item = document.createElement('li');
    item.textContent = finding;
    return item;
  }));
}

document.querySelector('.export-findings-close').addEventListener('click', () => {
  document.getElementById('export-findings').hidden = true;
});

// Fetched rather than linked, so an export that fails says why instead of downloading the error.
async function downloadExport(format) {
  const button = document.getElementById('export-button');
  const label = button.querySelector('.label');
  button.disabled = true;
  label.textContent = 'Exporting';
  document.getElementById('export-findings').hidden = true;
  // A .pptx makes a round trip to editide a tool call, so it reports how far it is.
  const polling = format === 'pptx' ? setInterval(async () => {
    try {
      const { fraction } = await (await fetch('api/export/progress', { cache: 'no-store' })).json();
      if (fraction != null && button.disabled) {
        label.textContent = `Exporting ${Math.round(fraction * 100)}%`;
      }
    } catch {
      // A missed poll leaves the last share showing.
    }
  }, 500) : null;
  try {
    const response = await fetch(`api/export/${format}`, { method: 'POST', cache: 'no-store' });
    if (!response.ok) {
      const text = await response.text();
      let message = text;
      try {
        message = JSON.parse(text).error;
      } catch {
        // A body that isn't the server's JSON is shown as it came.
      }
      throw new Error(message || `The export failed (${response.status}).`);
    }
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = downloadName(response.headers.get('Content-Disposition'), format);
    link.click();
    // Revoking at once can cancel the download before the browser reads the file.
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    showExportFindings(link.download, exportFindings(response.headers.get('Pintura-Findings')));
  } catch (failure) {
    // The notice lives in the one-slide view, so the grid shows the failure in the banner instead.
    const message = `Export to .${format} failed: ${failure.message}`;
    if (state.view === 'slide') {
      notice(message);
    } else {
      showError(message);
    }
  } finally {
    clearInterval(polling);
    button.disabled = false;
    label.textContent = 'Export';
    if (document.activeElement === document.body) {
      button.focus();
    }
  }
}

function exportMenuItems() {
  return [...document.getElementById('export-menu').querySelectorAll('[role="menuitem"]')];
}

// The browser opens and dismisses the popover; placing it under the button's right edge and moving focus is left to
// the page.
document.getElementById('export-menu').addEventListener('toggle', (event) => {
  const open = event.newState === 'open';
  const button = document.getElementById('export-button');
  button.setAttribute('aria-expanded', String(open));
  if (!open) {
    return;
  }
  const anchor = button.getBoundingClientRect();
  const menu = event.target;
  menu.style.top = `${anchor.bottom + 4}px`;
  menu.style.left = `${Math.max(8, anchor.right - menu.offsetWidth)}px`;
  exportMenuItems()[0].focus();
});

// The page's own keys stay out of the menu, or an arrow would change the slide behind it.
document.getElementById('export-menu').addEventListener('keydown', (event) => {
  event.stopPropagation();
  const items = exportMenuItems();
  const current = items.indexOf(document.activeElement);
  const moves = { ArrowDown: current + 1, ArrowUp: current - 1, Home: 0, End: items.length - 1 };
  if (event.key in moves) {
    event.preventDefault();
    items[(moves[event.key] + items.length) % items.length].focus();
  }
});

document.getElementById('export-menu').addEventListener('click', (event) => {
  const format = event.target.closest('[data-format]')?.dataset.format;
  if (format) {
    event.currentTarget.hidePopover();
    downloadExport(format);
  }
});

// #endregion

// #region Versions

function versionItems() {
  return [...document.getElementById('versions-menu').querySelectorAll('[role="menuitemradio"]')];
}

// The deck's versions, newest first, the newest standing for the last commit, with what each changed counted one at
// a time while the menu stays open, since counting loads the version and the one before it.
async function renderVersions(menu) {
  menu.replaceChildren();
  element('div', 'versions-note', menu).textContent = 'Loading versions…';
  let versions = [];
  try {
    versions = await (await fetch('api/versions', { cache: 'no-store' })).json();
  } catch {
    // An unreachable server leaves the note saying there's nothing to choose.
  }
  if (!menu.matches(':popover-open')) {
    return;
  }
  menu.replaceChildren();
  if (versions.length === 0) {
    element('div', 'versions-note', menu).textContent = 'No saved versions of this presentation yet';
    return;
  }
  const chosen = compareBase();
  const counts = [];
  versions.forEach((version, index) => {
    const latest = index === 0;
    const selected = latest ? chosen === 'HEAD' : chosen !== 'HEAD' && version.commit.startsWith(chosen);
    const button = element('button', 'version', menu);
    button.type = 'button';
    button.setAttribute('role', 'menuitemradio');
    button.setAttribute('aria-checked', String(selected));
    button.dataset.commit = latest ? '' : version.commit;
    const when = versionName(new Date(version.date));
    const head = element('span', 'version-head', button);
    element('span', 'version-when', head).textContent = latest ? `Last save · ${when}` : when;
    for (const tag of version.tags) {
      element('span', 'version-tag', head).textContent = tag;
    }
    const count = element('span', 'version-count', button);
    element('span', 'version-subject', button).textContent = version.mine ? version.subject
      : `${version.subject} — ${version.author}`;
    button.title = `${version.subject}\n${version.author} · ${version.commit.slice(0, 7)}`;
    counts.push({ commit: version.commit, count });
  });
  (versionItems().find((item) => item.getAttribute('aria-checked') === 'true') || versionItems()[0]).focus();
  for (const { commit, count } of counts) {
    if (!menu.matches(':popover-open')) {
      return;
    }
    try {
      const response = await fetch(`api/versions/changes?commit=${commit}`, { cache: 'no-store' });
      if (response.ok) {
        const { slides, first } = await response.json();
        count.textContent = first ? 'First version'
          : slides === 0 ? 'No visible change'
          : `${slides} ${slides === 1 ? 'slide' : 'slides'}`;
      }
    } catch {
      // A count that fails leaves its version uncounted.
    }
  }
}

document.getElementById('versions-menu').addEventListener('toggle', (event) => {
  const open = event.newState === 'open';
  document.getElementById('versions-button').setAttribute('aria-expanded', String(open));
  if (!open) {
    return;
  }
  const menu = event.target;
  const anchor = document.getElementById('versions-button').getBoundingClientRect();
  menu.style.top = `${anchor.bottom + 4}px`;
  menu.style.left = `${Math.max(8, Math.min(anchor.left, innerWidth - menu.offsetWidth - 8))}px`;
  renderVersions(menu);
});

document.getElementById('versions-menu').addEventListener('keydown', (event) => {
  event.stopPropagation();
  const items = versionItems();
  const current = items.indexOf(document.activeElement);
  const moves = { ArrowDown: current + 1, ArrowUp: current - 1, Home: 0, End: items.length - 1 };
  if (items.length > 0 && event.key in moves) {
    event.preventDefault();
    items[(moves[event.key] + items.length) % items.length].focus();
  }
});

document.getElementById('versions-menu').addEventListener('click', async (event) => {
  const item = event.target.closest('[role="menuitemradio"]');
  if (!item) {
    return;
  }
  event.currentTarget.hidePopover();
  chooseBase(item.dataset.commit);
  document.getElementById('versions-label').textContent = 'Comparing…';
  await load().catch((error) => showError(String(error)));
  renderToolbar();
  if (state.compare?.error) {
    const message = `Can't compare with that version: ${state.compare.error}`;
    if (state.view === 'slide') {
      notice(message);
    } else {
      showError(message);
    }
  }
});

// #endregion

// #region Chrome

function applyTheme() {
  document.documentElement.dataset.theme = state.theme;
  const button = document.getElementById('theme-toggle');
  const next = state.theme === 'dark' ? 'light' : 'dark';
  button.title = `Switch to the ${next} workspace`;
  button.setAttribute('aria-label', button.title);
}

function applyWidths() {
  const layout = document.getElementById('layout');
  const widths = [['--inspector-width', state.inspectorWidth], ['--sidebar-width', state.sidebarWidth],
    ['--strip-height', state.stripHeight]];
  for (const [property, width] of widths) {
    if (width) {
      layout.style.setProperty(property, `${width}px`);
    } else {
      layout.style.removeProperty(property);
    }
  }
}

function resized() {
  if (state.deck && state.view === 'slide') {
    renderSlide();
  }
}

// Dragging the edge of the inspector or the sidebar sets its width, or the top of the strip its height, kept between
// visits; a double click goes back to the default.
function installResizer(id, key, [least, most], width) {
  const resizer = document.getElementById(id);
  const apply = (value) => {
    state[key] = value;
    applyWidths();
    resized();
    if ((key === 'sidebarWidth' || key === 'stripHeight') && state.deck) {
      renderSidebar();
    }
  };
  resizer.addEventListener('pointerdown', (event) => {
    event.preventDefault();
    resizer.setPointerCapture(event.pointerId);
    document.body.classList.add('resizing');
    document.body.classList.toggle('resizing-rows', resizer.classList.contains('rows'));
    let frame = null;
    const move = (moved) => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => apply(Math.round(Math.min(most, Math.max(least, width(moved))))));
    };
    const end = () => {
      document.body.classList.remove('resizing', 'resizing-rows');
      resizer.removeEventListener('pointermove', move);
      save();
    };
    resizer.addEventListener('pointermove', move);
    resizer.addEventListener('pointerup', end, { once: true });
    resizer.addEventListener('pointercancel', end, { once: true });
  });
  resizer.addEventListener('dblclick', () => {
    apply(null);
    save();
  });
}

function showStatus(live) {
  state.live = live;
  renderStatus();
}

// The dot says whether the page is live, so the words can say how the presentation stands against its revision.
function renderStatus() {
  if (state.live === null) {
    return;
  }
  document.getElementById('status-dot').className = `status-indicator ${state.live ? 'live' : 'down'}`;
  const changed = comparing() ? state.compare.diff.slides.length : 0;
  const text = !state.live ? 'Reconnecting'
    : !comparing() ? 'Live'
    : changed === 0 ? 'No changes'
    : `${changed} ${changed === 1 ? 'slide' : 'slides'} changed`;
  document.getElementById('status-text').textContent = text;
  const button = document.getElementById('status');
  button.disabled = changed === 0;
  button.setAttribute('aria-pressed', String(filteringChanges()));
  const since = comparing() && namedBase() ? ` since ${baseName()}` : '';
  button.title = changed === 0 ? 'Redraws whenever a file is saved'
    : filteringChanges() ? `Showing only the slides changed${since}; click or press D to show every slide`
    : `Click or press D to show only the slides changed${since}`;
}

function toggleChangedOnly() {
  state.changedOnly = !state.changedOnly;
  save();
  renderSidebar();
  renderMain();
  renderStatus();
}

document.getElementById('status').addEventListener('click', toggleChangedOnly);

document.getElementById('theme-toggle').addEventListener('click', () => {
  state.theme = state.theme === 'dark' ? 'light' : 'dark';
  save();
  applyTheme();
});
document.getElementById('nav-hide').addEventListener('click', () => toggle('nav'));
document.getElementById('nav-show').addEventListener('click', () => toggle('nav'));
document.getElementById('strip-toggle').addEventListener('click', toggleStrip);
// A mouse wheel scrolls only up and down, which the strip turns into along it.
document.querySelector('.sidebar-scroll').addEventListener('wheel', (event) => {
  if (state.strip && !event.ctrlKey && Math.abs(event.deltaY) > Math.abs(event.deltaX)) {
    event.currentTarget.scrollLeft += event.deltaY;
    event.preventDefault();
  }
}, { passive: false });
document.getElementById('shortcuts-button').addEventListener('click', () =>
  document.getElementById('shortcuts').showModal());
document.getElementById('shortcuts-close').addEventListener('click', () =>
  document.getElementById('shortcuts').close());
applyTheme();
applyWidths();
installResizer('inspector-resizer', 'inspectorWidth', INSPECTOR_WIDTH, (event) => innerWidth - event.clientX);
installResizer('sidebar-resizer', 'sidebarWidth', SIDEBAR_WIDTH, (event) => event.clientX);
installResizer('strip-resizer', 'stripHeight', STRIP_HEIGHTS, (event) => innerHeight - event.clientY);

// #endregion

const events = new EventSource('api/events');
events.onopen = () => showStatus(true);
events.onerror = () => showStatus(false);
// An edit's own reload has usually drawn the version this announces already.
events.onmessage = (event) => {
  if (state.deck && String(state.deck.version) === event.data) {
    return;
  }
  load().catch((error) => showError(String(error)));
};
events.addEventListener('assets', () => location.reload());
// A commit or checkout changes what the slides compare against without changing them.
events.addEventListener('head', () => load().catch((error) => showError(String(error))));