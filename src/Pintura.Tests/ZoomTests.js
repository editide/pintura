const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { zoomStep, wheelZoom, anchoredScroll } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot',
  'navigation.js'));

test('zooming steps in and out from fit, stopping at either end', () => {
  // Act
  const steps = [zoomStep(1, 1), zoomStep(1, -1), zoomStep(8, 1), zoomStep(0.5, -1)];

  // Assert
  assert.deepEqual(steps, [1.5, 0.75, 8, 0.5]);
});

test('a step from between steps goes to the next one in that direction', () => {
  // Act
  const steps = [zoomStep(1.2, 1), zoomStep(1.2, -1)];

  // Assert
  assert.deepEqual(steps, [1.5, 1]);
});

test('a wheel notch zooms by the same amount in pixels or lines, and clamps', () => {
  // Act
  const pixels = wheelZoom(1, { deltaY: -100, deltaMode: 0 });
  const lines = wheelZoom(1, { deltaY: -3, deltaMode: 1 });
  const out = wheelZoom(1, { deltaY: 100, deltaMode: 0 });
  const far = wheelZoom(8, { deltaY: -1000, deltaMode: 0 });

  // Assert
  assert.ok(pixels > 1.15 && pixels < 1.3);
  assert.ok(Math.abs(lines - pixels) < 0.01);
  assert.ok(Math.abs(out * pixels - 1) < 1e-9);
  assert.equal(far, 8);
});

test('the point of the slide under the pointer stays there as the slide grows', () => {
  // Arrange
  const before = { left: 100, top: 50, width: 400, height: 225 };
  const after = { left: 24, top: 24, width: 800, height: 450 };

  // Act
  const scroll = anchoredScroll({ left: 0, top: 0 }, { x: 300, y: 50 }, before, after);

  // Assert
  assert.deepEqual(scroll, { left: 124, top: -26 });
});

test('an already scrolled view keeps its offset in the new scroll', () => {
  // Arrange
  const before = { left: -76, top: 24, width: 800, height: 450 };
  const after = { left: -76, top: 24, width: 1600, height: 900 };

  // Act
  const scroll = anchoredScroll({ left: 100, top: 0 }, { x: 324, y: 249 }, before, after);

  // Assert
  assert.deepEqual(scroll, { left: 500, top: 225 });
});