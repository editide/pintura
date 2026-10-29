const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { swipeStep, tapStep } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'navigation.js'));

test('a swipe left advances and a swipe right goes back', () => {
  // Arrange
  const start = { x: 200, y: 300 };

  // Act
  const left = swipeStep(start, { x: 120, y: 310 });
  const right = swipeStep(start, { x: 280, y: 290 });

  // Assert
  assert.deepEqual([left, right], [1, -1]);
});

test('a short or mostly vertical drag turns no slide', () => {
  // Arrange
  const start = { x: 200, y: 300 };

  // Act
  const short = swipeStep(start, { x: 180, y: 300 });
  const vertical = swipeStep(start, { x: 140, y: 400 });

  // Assert
  assert.deepEqual([short, vertical], [0, 0]);
});

test('a tap on the left third goes back and elsewhere advances', () => {
  // Arrange
  const frame = { left: 100, width: 300 };

  // Act
  const left = tapStep(150, frame);
  const middle = tapStep(250, frame);
  const right = tapStep(390, frame);

  // Assert
  assert.deepEqual([left, middle, right], [-1, 1, 1]);
});