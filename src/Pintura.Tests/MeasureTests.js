const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { countLines, overflow, fitted, shadowFilter, inkMargins, shortLastLine } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'render.js'));

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


test('a shadow falls toward its angle on the slide, however its item is rotated', () => {
  // Arrange
  const shadow = { color: '#1B1B1B', opacity: 40, blur: 4, distance: 3, angle: 90 };

  // Act
  const upright = shadowFilter(shadow);
  const rotated = shadowFilter(shadow, 90);

  // Assert
  assert.equal(upright, 'drop-shadow(0px 3px 4px rgba(27, 27, 27, 0.4))');
  assert.equal(rotated, 'drop-shadow(3px 0px 4px rgba(27, 27, 27, 0.4))');
});

// An RGBA image of the given size, opaque inside [left, top, right, bottom) and transparent around it.
function image(width, height, [left, top, right, bottom]) {
  const data = new Uint8ClampedArray(width * height * 4);
  for (let y = top; y < bottom; y++) {
    for (let x = left; x < right; x++) {
      data[(y * width + x) * 4 + 3] = 255;
    }
  }
  return data;
}

test("an image's transparent margins are fractions of its width and height", () => {
  // Arrange
  const data = image(100, 40, [4, 8, 96, 30]);

  // Act
  const margins = inkMargins(data, 100, 40);

  // Assert
  assert.deepEqual(margins, [0.04, 0.2, 0.04, 0.25]);
});

test("faint antialiasing at an image's edge is not ink", () => {
  // Arrange
  const data = image(10, 10, [2, 2, 8, 8]);
  data[3] = 4;

  // Act
  const margins = inkMargins(data, 10, 10);

  // Assert
  assert.deepEqual(margins, [0.2, 0.2, 0.2, 0.2]);
});

test('a blank image has no margins to report', () => {
  // Arrange
  const data = new Uint8ClampedArray(10 * 10 * 4);

  // Act
  const margins = inkMargins(data, 10, 10);

  // Assert
  assert.equal(margins, null);
});

test('a wrapped paragraph ending on two words starts its last line at the first of them', () => {
  // Arrange
  const text = 'Revenue grew in every region, led by Europe';
  const start = text.indexOf('by');

  // Act
  const line = shortLastLine(text, (index) => index >= start);

  // Assert
  assert.equal(line, start);
});

test('a last line of three words is not short', () => {
  // Arrange
  const text = 'Revenue grew in every region, led by Europe';
  const start = text.indexOf('led');

  // Act
  const line = shortLastLine(text, (index) => index >= start);

  // Assert
  assert.equal(line, null);
});

test('a paragraph on one line has no short last line', () => {
  // Arrange
  const text = 'Revenue grew';

  // Act
  const line = shortLastLine(text, () => true);

  // Assert
  assert.equal(line, null);
});

test('a line break the writer typed is not a wrap', () => {
  // Arrange
  const text = 'Revenue grew in every region\nEurope';
  const start = text.indexOf('Europe');

  // Act
  const line = shortLastLine(text, (index) => index >= start);

  // Assert
  assert.equal(line, null);
});

test('a word wrapped at its hyphen leaves its last part as the last line', () => {
  // Arrange
  const text = 'Margins improved year-over-year';
  const start = text.lastIndexOf('year');

  // Act
  const line = shortLastLine(text, (index) => index >= start);

  // Assert
  assert.equal(line, start);
});

test('spaces trailing a paragraph are not a word', () => {
  // Arrange
  const text = 'Revenue grew in every region, led by Europe  ';
  const start = text.indexOf('Europe');

  // Act
  const line = shortLastLine(text, (index) => index >= start && index < text.length - 2);

  // Assert
  assert.equal(line, start);
});