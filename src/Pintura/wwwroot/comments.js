'use strict';

// Reviewers' comments: a right-click on the slide pins one there, on the element under the pointer if any, and a pin
// opens it again to reword, reopen, or resolve. They live in comments.yaml beside the slides, where an agent reads them
// with --comments and marks each done or skipped with a note, leaving the pin here for the reviewer to check and resolve.

const COMMENT_HINT = 'Enter saves, Esc cancels';

const commenting = {
  // The open editor: its node, and the comment it adds or the one it rewords.
  open: null
};

// The corner of an item a comment on it counts from: a box's top-left, or the top-left of the span of a line's ends.
function itemOrigin(item) {
  if (item.frame) {
    return [item.frame[0], item.frame[1]];
  }
  if (item.from) {
    return [Math.min(item.from[0], item.to[0]), Math.min(item.from[1], item.to[1])];
  }
  return [0, 0];
}

// Where a comment sits on its slide, in points. One on an element moves with it and stays inside it; one whose element
// is gone sits at its offset from the slide's corner, marked as an orphan.
function commentPoint(comment, slide) {
  if (!comment.element) {
    return { x: comment.at[0], y: comment.at[1], orphan: false };
  }
  const item = (slide.items || []).find((candidate) => candidate.id === comment.element);
  if (!item) {
    return { x: comment.at[0], y: comment.at[1], orphan: true };
  }
  const [left, top] = itemOrigin(item);
  const [right, bottom] = item.frame
    ? [left + item.frame[2], top + item.frame[3]]
    : item.from ? [Math.max(item.from[0], item.to[0]), Math.max(item.from[1], item.to[1])] : [left, top];
  return { x: clamp(left + comment.at[0], left, right), y: clamp(top + comment.at[1], top, bottom), orphan: false };
}

// A new comment at a point on the slide, pinned to the item drawn at that index, or to the slide when there's none.
function commentDraft(slide, point, index) {
  const round = (value) => Math.round(value * 10) / 10;
  const item = index === null ? null : slide.items[index];
  if (!item) {
    return { slide: slide.id, at: [round(point.x), round(point.y)] };
  }
  const [left, top] = itemOrigin(item);
  return { slide: slide.id, element: item.id, at: [round(point.x - left), round(point.y - top)] };
}

function commentsOn(id) {
  return (state.deck?.comments || []).filter((comment) => comment.slide === id);
}

// Comments on slides the presentation no longer lists, which no pin shows.
function strayComments() {
  const ids = new Set(state.deck.slides.map((slide) => slide.id));
  return (state.deck.comments || []).filter((comment) => !ids.has(comment.slide));
}

function commentState(comment) {
  return comment.done ? 'done' : comment.skipped ? 'skipped' : 'open';
}

function commentCounts(comments) {
  const counts = { open: 0, done: 0, skipped: 0 };
  for (const comment of comments) {
    counts[commentState(comment)]++;
  }
  return counts;
}

// comments.yaml keeps dates as yyyy-MM-dd HH:mm so they sort; reviewers read them on the 12-hour clock.
function commentDate(date) {
  const match = /^(.*) (\d{2}):(\d{2})$/.exec(date || '');
  if (!match) {
    return date;
  }
  const hour = Number(match[2]);
  return `${match[1]} ${hour % 12 || 12}:${match[3]} ${hour < 12 ? 'AM' : 'PM'}`;
}

function commentTitle(comment) {
  const by = [comment.by, commentDate(comment.date)].filter(Boolean).join(', ');
  const note = comment.done ? `\nDone: ${comment.done}` : comment.skipped ? `\nSkipped: ${comment.skipped}` : '';
  return (by ? `${by}\n${comment.text}` : comment.text) + note;
}

// Numbered pins over the slide, outside its stage so the slide's edges don't clip them and they keep their size at
// any zoom.
function renderComments(slide, frame, size) {
  frame.querySelector(':scope > .comments')?.remove();
  const comments = commentsOn(slide.id);
  if (!state.comments || comments.length === 0) {
    return;
  }
  const scale = frame.offsetWidth / size[0];
  const layer = element('div', 'comments', frame);
  comments.forEach((comment, index) => {
    const point = commentPoint(comment, slide);
    const pin = element('button', `comment-pin ${commentState(comment)}${point.orphan ? ' orphan' : ''}`, layer);
    pin.type = 'button';
    pin.textContent = index + 1;
    pin.dataset.comment = comment.id;
    pin.style.left = `${point.x * scale}px`;
    pin.style.top = `${point.y * scale}px`;
    pin.title = point.orphan ? `${commentTitle(comment)}\n(its element ${comment.element} is gone)`
      : commentTitle(comment);
    pin.addEventListener('pointerdown', (event) => event.stopPropagation());
    pin.addEventListener('click', (event) => {
      event.stopPropagation();
      openComment(comment, pin.getBoundingClientRect());
    });
  });
}

// A slide's comments counted on its thumbnail or grid cell, a badge each for those open, done, and skipped.
function markComments(frame, id) {
  frame.querySelector(':scope > .comment-counts')?.remove();
  const counts = commentCounts(commentsOn(id));
  const kinds = Object.keys(counts).filter((kind) => counts[kind] > 0);
  if (kinds.length === 0) {
    return;
  }
  const badges = element('span', 'comment-counts', frame);
  badges.title = kinds.map((kind) => `${counts[kind]} ${kind}`).join(', ');
  for (const kind of kinds) {
    element('span', `comment-count ${kind}`, badges).textContent = kind === 'done' ? `✓${counts.done}`
      : counts[kind];
  }
}

// Above the slides, once an agent has marked comments: how many are open, done, and skipped, and a way to resolve
// every done one at once.
function renderCommentSummary() {
  const container = document.getElementById('comment-summary');
  container.replaceChildren();
  const comments = state.deck.comments || [];
  const counts = commentCounts(comments);
  container.hidden = counts.done + counts.skipped === 0;
  if (container.hidden) {
    return;
  }
  element('span', null, container).textContent = Object.keys(counts).filter((kind) => counts[kind] > 0)
    .map((kind) => `${counts[kind]} ${kind}`).join(' · ');
  if (counts.done > 0) {
    const resolve = element('button', 'tool', container);
    resolve.type = 'button';
    resolve.textContent = 'Resolve done';
    resolve.title = 'Delete every comment marked done';
    resolve.addEventListener('click', () => sendComment({
      many: comments.filter((comment) => comment.done).map((comment) => ({ remove: comment.id }))
    }, state.current));
  }
}

function renderStrayComments() {
  const container = document.getElementById('stray-comments');
  container.replaceChildren();
  const stray = strayComments();
  if (stray.length === 0) {
    return;
  }
  element('div', 'section-name', container).textContent = 'Comments on removed slides';
  for (const comment of stray) {
    const row = element('div', 'stray-comment', container);
    const words = element('span', null, row);
    words.textContent = `${comment.slide}: ${comment.text}`;
    words.title = commentTitle(comment);
    const remove = element('button', 'icon-button', row);
    remove.type = 'button';
    remove.textContent = '×';
    remove.title = 'Delete this comment';
    remove.setAttribute('aria-label', 'Delete this comment');
    remove.addEventListener('click', () => sendComment({ remove: comment.id }, comment.slide));
  }
}

function sendComment(change, slide, kind = 'edit') {
  return post('api/comments', change, (undo) => (undo ? { slide, comment: undo } : null), kind);
}

// A right-click on the slide opens an editor for a new comment there.
function startComment(event) {
  const stage = event.target.closest('#slide-frame .stage');
  if (!stage || state.view !== 'slide' || state.presenting || reviewing() || editingActive()) {
    return;
  }
  event.preventDefault();
  const slide = currentSlide();
  const box = stage.getBoundingClientRect();
  const scale = box.width / stage.offsetWidth;
  const point = { x: (event.clientX - box.left) / scale, y: (event.clientY - box.top) / scale };
  const node = event.target.closest('.item');
  const draft = commentDraft(slide, point, node ? Number(node.dataset.index) : null);
  openComment(draft, { left: event.clientX, top: event.clientY, right: event.clientX, bottom: event.clientY });
}

// The editor beside a point on screen: empty for a new comment, or holding an existing one's words, and the note an
// agent marked it with, with ways to reopen and resolve it.
function openComment(comment, anchor) {
  closeComment();
  const existing = Boolean(comment.id);
  const node = element('div', 'comment-editor', document.body);
  node.setAttribute('role', 'dialog');
  node.setAttribute('aria-label', existing ? 'Comment' : 'New comment');
  if (existing && (comment.by || comment.date)) {
    element('div', 'comment-by', node).textContent = [comment.by, commentDate(comment.date)].filter(Boolean).join(', ');
  }
  const words = element('textarea', null, node);
  words.rows = 3;
  words.value = existing ? comment.text : '';
  words.placeholder = existing ? '' : `Comment on ${comment.element || 'the slide'}`;
  const marked = commentState(comment);
  if (existing && marked !== 'open') {
    const note = element('div', `comment-note ${marked}`, node);
    element('strong', null, note).textContent = marked === 'done' ? 'Done' : 'Skipped';
    element('span', null, note).textContent = comment[marked];
  }
  const foot = element('div', 'comment-foot', node);
  element('span', 'comment-hint', foot).textContent = COMMENT_HINT;
  if (existing && marked !== 'open') {
    const reopen = element('button', 'tool', foot);
    reopen.type = 'button';
    reopen.textContent = 'Reopen';
    reopen.title = 'Drop the note and leave the comment for an agent again; rewording it does too';
    reopen.addEventListener('click', () => {
      closeComment();
      sendComment({ reopen: comment.id }, comment.slide);
    });
  }
  if (existing) {
    const resolve = element('button', 'tool', foot);
    resolve.type = 'button';
    resolve.textContent = 'Resolve';
    resolve.title = 'Delete this comment';
    resolve.addEventListener('click', () => {
      closeComment();
      sendComment({ remove: comment.id }, comment.slide);
    });
  }
  words.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      closeComment();
    } else if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      saveComment();
    }
  });
  commenting.open = { node, comment, words };
  const width = 300;
  node.style.left = `${clamp(anchor.right + 8, 8, innerWidth - width - 8)}px`;
  node.style.top = `${clamp(anchor.top, 8, innerHeight - node.offsetHeight - 8)}px`;
  words.focus();
}

// Saves what the editor holds: a new comment, new words for an existing one, which reopen it, or, emptied, resolving
// it.
function saveComment() {
  const open = commenting.open;
  if (!open) {
    return;
  }
  closeComment();
  const text = open.words.value.trim();
  const { comment } = open;
  if (!comment.id) {
    if (text) {
      sendComment({ add: { ...comment, text } }, comment.slide);
    }
  } else if (!text) {
    sendComment({ remove: comment.id }, comment.slide);
  } else if (text !== comment.text) {
    sendComment({ id: comment.id, text }, comment.slide);
  }
}

function closeComment() {
  commenting.open?.node.remove();
  commenting.open = null;
}

// The next slide after the current one with comments, or before it, wrapping around.
function stepComments(offset) {
  const slides = state.deck.slides;
  const index = slides.indexOf(currentSlide());
  for (let distance = 1; distance <= slides.length; distance++) {
    const slide = slides[(index + offset * distance + slides.length * distance) % slides.length];
    if (commentsOn(slide.id).length > 0) {
      select(slide.id);
      return;
    }
  }
  notice('No slide has comments');
}

function installComments(slideView) {
  slideView.addEventListener('contextmenu', startComment);
  // Clicking away keeps what was written rather than losing it.
  document.addEventListener('pointerdown', (event) => {
    if (commenting.open && !commenting.open.node.contains(event.target)) {
      saveComment();
    }
  }, true);
}

if (typeof module !== 'undefined') {
  module.exports = { commentPoint, commentDraft, commentCounts, commentDate };
}