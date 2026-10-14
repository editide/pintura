const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const wwwroot = path.join(__dirname, '..', 'Pintura', 'wwwroot');
// The page's scripts share one scope, where shapes.js defines the clamp comments.js uses.
globalThis.clamp = require(path.join(wwwroot, 'shapes.js')).clamp;
const { commentPoint, commentDraft, commentCounts, commentDate } = require(path.join(wwwroot, 'comments.js'));

const slide = {
  id: 'revenue',
  items: [
    { id: 'title', frame: [40, 20, 600, 50] },
    { id: 'rule', from: [300, 400], to: [100, 380] },
    { id: 'chart', frame: [40, 100, 400, 300] }
  ]
};

test('a comment on an element follows the element from its top-left corner', () => {
  // Act
  const point = commentPoint({ slide: 'revenue', element: 'chart', at: [10, 20] }, slide);

  // Assert
  assert.deepEqual(point, { x: 50, y: 120, orphan: false });
});

test('a comment stays inside its element after the element shrinks', () => {
  // Act
  const point = commentPoint({ slide: 'revenue', element: 'title', at: [900, 80] }, slide);

  // Assert
  assert.deepEqual(point, { x: 640, y: 70, orphan: false });
});

test('a comment on a line counts from the corner its ends span', () => {
  // Act
  const point = commentPoint({ slide: 'revenue', element: 'rule', at: [5, 5] }, slide);

  // Assert
  assert.deepEqual(point, { x: 105, y: 385, orphan: false });
});

test('a comment on the slide, or on an element that is gone, sits at its point on the slide', () => {
  // Act
  const loose = commentPoint({ slide: 'revenue', at: [480, 270] }, slide);
  const orphan = commentPoint({ slide: 'revenue', element: 'logo', at: [12, 8] }, slide);

  // Assert
  assert.deepEqual(loose, { x: 480, y: 270, orphan: false });
  assert.deepEqual(orphan, { x: 12, y: 8, orphan: true });
});

test('a right-click on an element pins the comment to it, and elsewhere to the slide', () => {
  // Act
  const onChart = commentDraft(slide, { x: 140.26, y: 180 }, 2);
  const onSlide = commentDraft(slide, { x: 700, y: 500 }, null);

  // Assert
  assert.deepEqual(onChart, { slide: 'revenue', element: 'chart', at: [100.3, 80] });
  assert.deepEqual(onSlide, { slide: 'revenue', at: [700, 500] });
});

test('a slide counts its comments as open, done, or skipped', () => {
  // Arrange
  const comments = [{ text: 'a' }, { text: 'b', done: 'Did it' }, { text: 'c', skipped: 'No data' }, { text: 'd' }];

  // Act
  const counts = commentCounts(comments);

  // Assert
  assert.deepEqual(counts, { open: 2, done: 1, skipped: 1 });
});

test('a comment shows its time on the 12-hour clock', () => {
  // Act
  const dates = ['2026-10-07 00:05', '2026-10-07 09:30', '2026-10-07 12:00', '2026-10-07 15:45', 'yesterday'].map(commentDate);

  // Assert
  assert.deepEqual(dates, ['2026-10-07 12:05 AM', '2026-10-07 9:30 AM', '2026-10-07 12:00 PM', '2026-10-07 3:45 PM', 'yesterday']);
});
