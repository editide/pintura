const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { countLines, overflow, fitted } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'render.js'));

test('runs side by side on one line count once', () => {
  // Arrange
  const rects = [{ top: 0, bottom: 19 }, { top: 0, bottom: 19 }, { top: 0, bottom: 19 }];

  // Act
  const lines = countLines(rects);

  // Assert
  assert.equal(lines, 1);
});

test('a wrapped paragraph counts a line per band of rectangles', () => {
  // Arrange
  const rects = [{ top: 0, bottom: 19 }, { top: 19.2, bottom: 38.2 }, { top: 38.4, bottom: 57.4 }];

  // Act
  const lines = countLines(rects);

  // Assert
  assert.equal(lines, 3);
});

test('a small run beside a large one shares its line', () => {
  // Arrange
  const rects = [{ top: 0, bottom: 46 }, { top: 26, bottom: 40 }];

  // Act
  const lines = countLines(rects);

  // Assert
  assert.equal(lines, 1);
});

test('tight line spacing overlaps neighbouring lines without merging them', () => {
  // Arrange
  const rects = [{ top: 0, bottom: 19 }, { top: 15.4, bottom: 34.4 }];

  // Act
  const lines = countLines(rects);

  // Assert
  assert.equal(lines, 2);
});

test('text continuing at the top of the next column starts a new line', () => {
  // Arrange
  const rects = [{ top: 0, bottom: 19 }, { top: 19, bottom: 38 }, { top: 0, bottom: 19 }];

  // Act
  const lines = countLines(rects);

  // Assert
  assert.equal(lines, 3);
});

test('empty rectangles are ignored', () => {
  // Arrange
  const rects = [{ top: 0, bottom: 19 }, { top: 40, bottom: 40 }];

  // Act
  const lines = countLines(rects);

  // Assert
  assert.equal(lines, 1);
});

test('unfitted text reaching into the insets stays inside its frame', () => {
  // Arrange
  const frame = { top: 0, bottom: 14.4 };
  const content = { top: 3.6, bottom: 10.8 };
  const text = { top: 1.2, bottom: 13.2 };

  // Act
  const past = overflow('none', text, frame, content);

  // Assert
  assert.equal(past, 0);
});

test('unfitted text centred in a short frame runs past it on both sides by half', () => {
  // Arrange
  const frame = { top: 0, bottom: 10 };
  const content = { top: 2, bottom: 8 };
  const text = { top: -3, bottom: 13 };

  // Act
  const past = overflow('none', text, frame, content);

  // Assert
  assert.equal(past, 3);
});

test('unfitted text anchored at the top runs past the bottom of its frame', () => {
  // Arrange
  const frame = { top: 0, bottom: 40 };
  const content = { top: 3.6, bottom: 36.4 };
  const text = { top: 3.6, bottom: 62.8 };

  // Act
  const past = overflow('none', text, frame, content);

  // Assert
  assert.equal(past, 22.8);
});

test('shrunk text must fit inside the insets, as PowerPoint shrinks it to', () => {
  // Arrange
  const frame = { top: 0, bottom: 14.4 };
  const content = { top: 3.6, bottom: 10.8 };
  const text = { top: 1.2, bottom: 13.2 };

  // Act
  const past = overflow('shrink', text, frame, content);

  // Assert
  assert.equal(past, 4.8);
});

test('text that grows its shape never overflows', () => {
  // Arrange
  const frame = { top: 0, bottom: 10 };
  const content = { top: 2, bottom: 8 };
  const text = { top: 2, bottom: 30 };

  // Act
  const past = overflow('grow', text, frame, content);

  // Assert
  assert.equal(past, 0);
});

test('a contained picture is outlined where it is drawn, and a covered or stretched one at its frame', () => {
  // Act
  const contained = fitted('contain', 200, 100, [400, 400]);
  const covered = fitted('cover', 200, 100, [400, 400]);
  const stretched = fitted('stretch', 200, 100, [400, 400]);

  // Assert
  assert.deepEqual(contained, [50, 0, 100, 100]);
  assert.deepEqual(covered, [0, 0, 200, 100]);
  assert.deepEqual(stretched, [0, 0, 200, 100]);
});
