'use strict';

// A pick in the slide view: its variants side by side in the slide's place, in an order shuffled once per pick so
// position can't favour one, each under its intent with Keep beneath it, and None below them all. Clicking a variant
// enlarges it, to flip through them with the arrows; Keep or None then asks why in a dialog. Nothing here says which
// variant the agent expects kept.

const PICK_GAP = 20;
const PICK_ORDERS = 'pintura.picks';
// Beside an enlarged variant, room for the arrows either side, and above and below it, for its intent and Keep.
const ENLARGED_SIDES = 136;
const ENLARGED_ENDS = 150;

// The variant enlarged, by pick, or null for the overview.
const picking = { pick: null, index: null };

function pickOf(slide) {
  return slide?.pick ? state.deck.picks.find((pick) => pick.id === slide.pick) || null : null;
}

// The slide the deck draws in a variant's place: its pick's first variant.
function deckSlide(slide) {
  return slide?.pick ? state.deck.slides.find((candidate) => candidate.pick === slide.pick) || slide : slide;
}

function offeredSlide(id) {
  return state.deck.slides.find((slide) => slide.id === id) || state.deck.variants.find((slide) => slide.id === id);
}

function variantLetter(index) {
  return String.fromCharCode(65 + index);
}

function showingPick(slide) {
  return state.view === 'slide' && !state.layout && !state.presenting && Boolean(pickOf(slide));
}

// The variants as shown, shuffled the first time the pick is, and kept so in this browser so the order recorded is
// the order seen however often the page redraws.
function shownVariants(pick) {
  const orders = JSON.parse(localStorage.getItem(PICK_ORDERS) || '{}');
  const slides = pick.variants.map((variant) => variant.slide);
  let order = orders[pick.id];
  if (!order || order.length !== slides.length || !slides.every((slide) => order.includes(slide))) {
    order = shuffled(slides);
    orders[pick.id] = order;
    localStorage.setItem(PICK_ORDERS, JSON.stringify(orders));
  }
  return order.map((slide) => pick.variants.find((variant) => variant.slide === slide));
}

function shuffled(items, random = Math.random) {
  const copy = [...items];
  for (let index = copy.length - 1; index > 0; index--) {
    const other = Math.floor(random() * (index + 1));
    [copy[index], copy[other]] = [copy[other], copy[index]];
  }
  return copy;
}

// A thumbnail or grid cell draws a pick as a card saying how many variants wait, rather than the first variant, which
// would show one of them before the others and favour it.
function mountThumbnail(frame, slide, size, width) {
  const pick = pickOf(slide);
  if (!pick) {
    return mountStage(frame, slide, size, width);
  }
  frame.replaceChildren();
  frame.style.width = `${width}px`;
  frame.style.height = `${width * size[1] / size[0]}px`;
  const card = element('div', 'pick-card', frame);
  card.style.fontSize = `${Math.max(10, width / 14)}px`;
  element('div', 'pick-card-count', card).textContent = `${pick.variants.length} variants`;
  element('div', 'pick-card-ask', card).textContent = 'Keep one';
  return card;
}

// A pick's caption, which names none of its variants.
function thumbnailTitle(slide) {
  const pick = pickOf(slide);
  return pick ? `Keep one of ${pick.variants.length} variants` : slide.title;
}

function enlargedIndex(pick) {
  return picking.pick === pick.id ? picking.index : null;
}

function renderPick(slide) {
  const pick = pickOf(slide);
  const view = document.getElementById('pick-view');
  const size = state.deck.size;
  const variants = shownVariants(pick);
  const enlarged = enlargedIndex(pick);
  const width = enlarged === null
    ? Math.max(160, Math.floor((view.clientWidth - 48 - PICK_GAP * (variants.length - 1)) / variants.length))
    : Math.max(200, Math.floor(Math.min(view.clientWidth - 2 * ENLARGED_SIDES,
      (view.clientHeight - ENLARGED_ENDS) * size[0] / size[1])));
  const slides = variants.map((variant) => offeredSlide(variant.slide) || null);
  const drawn = JSON.stringify([size, width, pick, variants, slides, enlarged]);
  if (view.dataset.drawn === drawn) {
    return;
  }
  view.dataset.drawn = drawn;
  view.replaceChildren();
  view.classList.toggle('enlarged', enlarged !== null);
  if (enlarged === null) {
    const row = element('div', 'pick-variants', view);
    row.style.gap = `${PICK_GAP}px`;
    variants.forEach((variant, index) => {
      const column = renderVariant(row, pick, variant, index, slides[index], width);
      const frame = column.querySelector('.pick-frame');
      frame.title = 'Enlarge, to flip through the variants';
      frame.addEventListener('click', () => enlarge(pick, index));
    });
    const none = element('button', 'tool pick-none', view);
    none.type = 'button';
    none.textContent = 'None of these';
    none.addEventListener('click', () => askWhy(pick, null));
    return;
  }
  const stage = element('div', 'pick-enlarged', view);
  const back = arrowButton(stage, '‹', 'Previous variant (←)', () => flipVariant(-1));
  renderVariant(stage, pick, variants[enlarged], enlarged, slides[enlarged], width);
  const next = arrowButton(stage, '›', 'Next variant (→)', () => flipVariant(1));
  back.disabled = enlarged === 0;
  next.disabled = enlarged === variants.length - 1;
  const close = element('button', 'icon-button pick-close', view);
  close.type = 'button';
  close.textContent = '×';
  close.title = 'Back to all the variants (Escape)';
  close.setAttribute('aria-label', close.title);
  close.addEventListener('click', () => enlarge(pick, null));
}

// A variant under its letter and intent, with Keep beneath it.
function renderVariant(parent, pick, variant, index, shown, width) {
  const size = state.deck.size;
  const column = element('div', 'pick-variant', parent);
  column.style.width = `${width}px`;
  const intent = element('div', 'pick-intent', column);
  element('span', 'pick-letter', intent).textContent = variantLetter(index);
  element('span', null, intent).textContent = variant.intent;
  const frame = element('button', 'pick-frame', column);
  frame.type = 'button';
  frame.dataset.slide = variant.slide;
  if (shown && !shown.error) {
    mountStage(frame, shown, size, width);
  } else {
    const stage = mountStage(frame, { items: [], background: '#fff' }, size, width);
    element('div', 'absent', stage).textContent = shown?.error || `No slides/${variant.slide}.yaml`;
  }
  const keep = element('button', 'tool primary pick-keep', column);
  keep.type = 'button';
  keep.textContent = `Keep ${variantLetter(index)}`;
  keep.addEventListener('click', () => askWhy(pick, variant.slide));
  return column;
}

function arrowButton(parent, text, title, action) {
  const button = element('button', 'icon-button pick-arrow', parent);
  button.type = 'button';
  button.textContent = text;
  button.title = title;
  button.setAttribute('aria-label', title);
  button.addEventListener('click', action);
  return button;
}

function enlarge(pick, index) {
  picking.pick = index === null ? null : pick.id;
  picking.index = index;
  renderSlide();
}

// Steps the enlarged variant, stopping at either end. Returns whether a variant was enlarged to step.
function flipVariant(offset) {
  const slide = currentSlide();
  const pick = pickOf(slide);
  const index = pick && showingPick(slide) ? enlargedIndex(pick) : null;
  if (index === null) {
    return false;
  }
  enlarge(pick, Math.min(Math.max(index + offset, 0), pick.variants.length - 1));
  return true;
}

// Arrows flip the enlarged variant and Escape returns to all of them. Returns whether the key was one of them.
function pickKey(event) {
  const offsets = { ArrowRight: 1, ArrowDown: 1, ' ': 1, ArrowLeft: -1, ArrowUp: -1 };
  if (event.key in offsets) {
    return flipVariant(offsets[event.key]);
  }
  const pick = pickOf(currentSlide());
  if (event.key === 'Escape' && pick && enlargedIndex(pick) !== null) {
    enlarge(pick, null);
    return true;
  }
  return false;
}

// Keep or None asks why in a dialog, its box focused: optional for Keep, needed for None.
function askWhy(pick, keep) {
  if (editingActive()) {
    return;
  }
  const variants = shownVariants(pick);
  const letter = keep && variantLetter(variants.findIndex((variant) => variant.slide === keep));
  const dialog = document.getElementById('pick-why');
  const why = dialog.querySelector('textarea');
  const confirm = dialog.querySelector('.pick-confirm');
  dialog.querySelector('h2').textContent = keep ? `Keep ${letter}` : 'None of these';
  why.placeholder = keep ? 'Why this one? (optional)' : 'Why none of them?';
  why.value = '';
  confirm.textContent = keep ? `Keep ${letter}` : 'Keep none';
  confirm.disabled = !keep;
  why.oninput = () => {
    confirm.disabled = !keep && why.value.trim().length === 0;
  };
  why.onkeydown = (event) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      confirm.click();
    }
  };
  confirm.onclick = () => {
    if (confirm.disabled) {
      return;
    }
    dialog.close();
    resolvePick(pick, keep, why.value.trim(), variants.map((variant) => variant.slide));
  };
  dialog.showModal();
  why.focus();
}

// Keeps a variant, or with none, the first in place, and shows the slide kept.
function resolvePick(pick, keep, why, shown) {
  picking.pick = null;
  picking.index = null;
  select(keep || pick.variants[0].slide);
  sendPick({ pick: pick.id, keep, why: why || null, shown }, pick.variants[0].slide);
}

// Undoing a keep shows the pick reopened, in its first variant's place, and redoing it the slide kept.
function sendPick(change, first, kind = 'edit') {
  return post('api/picks', change,
    (undo) => (undo ? { slide: undo.reopen ? first : undo.keep || first, first, pick: undo } : null), kind);
}

function installPicks() {
  const dialog = document.getElementById('pick-why');
  dialog.querySelector('.pick-cancel').addEventListener('click', () => dialog.close());
}

if (typeof module !== 'undefined') {
  module.exports = { variantLetter, shuffled };
}