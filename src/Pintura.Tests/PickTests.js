const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { resolvedVariants, shuffled, variantLetter } =
  require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'picks.js'));

test('shuffling keeps every variant once and moves them by the draw', () => {
  // Arrange
  const draws = [0.1, 0.9];
  const random = () => draws.shift();

  // Act
  const order = shuffled(['a', 'b', 'c'], random);

  // Assert
  assert.deepEqual(order, ['c', 'b', 'a']);
});

test('variants are lettered from A in the order shown', () => {
  // Act
  const letters = [0, 1, 2].map(variantLetter);

  // Assert
  assert.deepEqual(letters, ['A', 'B', 'C']);
});


test('a resolved pick shows its variants in the order they were seen', () => {
  // Arrange
  const pick = { variants: [{ slide: 'two' }, { slide: 'stacked' }, { slide: 'waterfall' }] };

  // Act
  const order = resolvedVariants(pick, { shown: ['waterfall', 'two', 'stacked'] });

  // Assert
  assert.deepEqual(order.map((variant) => variant.slide), ['waterfall', 'two', 'stacked']);
});

test('a resolution with no order recorded shows the variants as offered', () => {
  // Arrange
  const pick = { variants: [{ slide: 'two' }, { slide: 'stacked' }] };

  // Act
  const order = resolvedVariants(pick, { shown: null });

  // Assert
  assert.deepEqual(order.map((variant) => variant.slide), ['two', 'stacked']);
});
