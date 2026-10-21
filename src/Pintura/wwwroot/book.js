'use strict';

// The book a traced number opens, slid in over the whole page: Libreta's exported page in a sent file, or its live
// page in the preview. The frame stays loaded when the book closes, so the next number opens it at once.

const book = document.getElementById('book');
const bookFrame = document.getElementById('book-frame');
// The history length once the open book's entry was pushed, so closing knows whether clicks in the book added more.
let bookHistory = 0;

function showBook(slide) {
  document.getElementById('book-back-label').textContent =
    slide ? `Slide ${slide.number}${slide.title ? ` · ${slide.title}` : ''}` : 'Back';
  // Back closes the book, after stepping back through whatever was clicked in it, whose history the frame shares.
  if (!history.state?.book) {
    history.pushState({ ...history.state, book: true }, '');
    bookHistory = history.length;
  }
  book.classList.add('open');
}

function bookShown() {
  return Boolean(book?.classList.contains('open'));
}

// Leaving the book's entry closes it as back does. Entries clicked in the book can't be dropped, so they stay.
function closeBook() {
  if (history.state?.book && history.length === bookHistory) {
    history.back();
  }
  hideBook();
}

function hideBook() {
  book.classList.remove('open');
  // Focus left inside the hidden frame would keep the page's keys from reaching it.
  bookFrame.blur();
  window.focus();
}

// The book follows the deck's theme, which Libreta on another origin can't read.
function themeBook() {
  bookFrame?.contentWindow.postMessage({ pintura: 'theme', theme: document.documentElement.dataset.theme }, '*');
}

// A number's cell replaces the frame's page, since an entry for each would make back step through them.
function showInBook(url) {
  bookFrame.dataset.page = url;
  bookFrame.contentWindow.location.replace(url);
}

if (book) {
  document.getElementById('book-back').addEventListener('click', closeBook);
  bookFrame.addEventListener('load', themeBook);
  window.addEventListener('popstate', () => {
    if (bookShown() && !history.state?.book) {
      hideBook();
    }
  });
  window.addEventListener('message', (event) => {
    if (event.source === bookFrame.contentWindow && event.data?.libreta === 'escape') {
      closeBook();
    }
  });
}
