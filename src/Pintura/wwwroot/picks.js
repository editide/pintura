'use strict';

// A pick in the slide view: its variants side by side in the slide's place, each under its intent with Keep beneath
// it, and None, which needs a reason, below them all. Clicking a variant opens it alone, to comment on or edit as any
// slide; nothing here says which variant the agent expects kept.

const PICK_GAP = 20;

// What the reviewer has typed as why, by pick, kept while the page redraws.
const pickReasons = new Map();

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

// A later variant is only ever shown alone; the first is the pick's place in the deck, which shows them all.
function laterVariant(id) {
  return state.deck.variants.some((slide) => slide.id === id);
}

function variantLetter(index) {
  return String.fromCharCode(65 + index);
}

function showingPick(slide) {
  return state.view === 'slide' && !state.layout && !state.alone && !state.presenting && Boolean(pickOf(slide));
}

// The marks a thumbnail or grid cell of a pick carries: how many variants wait to be picked from.
function markPick(node, slide) {
  const pick = pickOf(slide);
  node.querySelector(':scope > .pick-mark')?.remove();
  if (pick) {
    const mark = element('span', 'pick-mark', node);
    mark.textContent = `${pick.variants.length} variants`;
    mark.title = `Keep one of ${pick.variants.length} variants`;
  }
}

function renderPick(slide) {
  const pick = pickOf(slide);
  const view = document.getElementById('pick-view');
  const size = state.deck.size;
  const count = pick.variants.length;
  const width = Math.max(160, Math.floor((view.clientWidth - 48 - PICK_GAP * (count - 1)) / count));
  const variants = pick.variants.map((variant) => offeredSlide(variant.slide) || null);
  const drawn = JSON.stringify([size, width, pick, variants, pick.variants.map((variant) =>
    commentsOn(variant.slide)), state.comments]);
  if (view.dataset.drawn === drawn) {
    return;
  }
  const focused = document.activeElement?.classList.contains('pick-why') ? document.activeElement : null;
  const selection = focused && [focused.selectionStart, focused.selectionEnd];
  view.dataset.drawn = drawn;
  view.replaceChildren();
  element('div', 'pick-head', view).textContent = `${slide.number} of ${state.deck.slides.length} · Keep one of ` +
    `${count} variants${pick.by ? `, offered by ${pick.by}` : ''}`;
  const row = element('div', 'pick-variants', view);
  row.style.gap = `${PICK_GAP}px`;
  pick.variants.forEach((variant, index) => {
    const shown = variants[index];
    const column = element('div', 'pick-variant', row);
    column.style.width = `${width}px`;
    const intent = element('div', 'pick-intent', column);
    element('span', 'pick-letter', intent).textContent = variantLetter(index);
    element('span', null, intent).textContent = variant.intent;
    const frame = element('button', 'pick-frame', column);
    frame.type = 'button';
    frame.dataset.slide = variant.slide;
    frame.title = `Open ${variant.slide} alone, to comment on or edit it`;
    if (shown && !shown.error) {
      mountStage(frame, shown, size, width);
    } else {
      const stage = mountStage(frame, { items: [], background: '#fff' }, size, width);
      element('div', 'absent', stage).textContent = shown?.error || `No slides/${variant.slide}.yaml`;
    }
    if (state.comments) {
      markComments(frame, variant.slide);
    }
    frame.addEventListener('click', () => select(variant.slide, true));
    const keep = element('button', 'tool primary pick-keep', column);
    keep.type = 'button';
    keep.textContent = `Keep ${variantLetter(index)}`;
    keep.title = `Keep ${variant.slide} and delete the other variants`;
    keep.addEventListener('click', () => resolvePick(pick, variant.slide));
  });
  const foot = element('div', 'pick-foot', view);
  const why = element('textarea', 'pick-why', foot);
  why.rows = 2;
  why.placeholder = 'Why: optional for Keep, needed for None';
  why.setAttribute('aria-label', 'Why');
  why.value = pickReasons.get(pick.id) || '';
  const none = element('button', 'tool pick-none', foot);
  none.type = 'button';
  none.textContent = 'None';
  none.title = 'None of these will do: keep the first in place and record why';
  none.disabled = why.value.trim().length === 0;
  why.addEventListener('input', () => {
    pickReasons.set(pick.id, why.value);
    none.disabled = why.value.trim().length === 0;
  });
  // Typing stays in the box: the page shouldn't treat its keys as shortcuts.
  why.addEventListener('keydown', (event) => event.stopPropagation());
  none.addEventListener('click', () => resolvePick(pick, null));
  element('div', 'pick-hint', foot).textContent = 'Click a variant to see it alone, comment on it, or edit it.';
  if (focused) {
    why.focus();
    why.setSelectionRange(...selection);
  }
}

// Above a variant shown alone: which it is, what it tries to show, and the way back to all of them.
function renderVariantBar(slide) {
  const bar = document.getElementById('variant-bar');
  const pick = state.alone && !state.layout ? pickOf(slide) : null;
  bar.hidden = !pick;
  if (!pick) {
    return;
  }
  const index = pick.variants.findIndex((variant) => variant.slide === slide.id);
  bar.replaceChildren();
  element('span', 'pick-letter', bar).textContent = variantLetter(index);
  element('span', 'variant-intent', bar).textContent = pick.variants[index].intent;
  const back = element('button', 'tool', bar);
  back.type = 'button';
  back.textContent = `All ${pick.variants.length} variants`;
  back.title = 'Show the variants side by side, to keep one';
  back.addEventListener('click', () => select(deckSlide(slide).id));
}

// Keeps a variant, or with none, the first in place, and shows the slide kept.
function resolvePick(pick, keep) {
  if (editingActive()) {
    return;
  }
  const why = (pickReasons.get(pick.id) || '').trim();
  if (!keep && !why) {
    return;
  }
  select(keep || pick.variants[0].slide);
  sendPick({ pick: pick.id, keep, why: why || null }, pick.variants[0].slide);
}

// Undoing a keep shows the pick reopened, in its first variant's place, and redoing it the slide kept.
function sendPick(change, first, kind = 'edit') {
  return post('api/picks', change,
    (undo) => (undo ? { slide: undo.reopen ? first : undo.keep || first, first, pick: undo } : null), kind);
}

if (typeof module !== 'undefined') {
  module.exports = { variantLetter };
}