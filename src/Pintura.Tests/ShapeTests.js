const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { shapeCommands, shapePath, isOpen, dashArray } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot',
  'shapes.js'));

function points(commands) {
  return commands.filter(([command]) => command !== 'Z').map(([, ...coordinates]) => coordinates.slice(-2));
}

test('a corner adjustment is a radius in points, whatever the size of the box', () => {
  // Arrange
  const small = shapeCommands('rounded_rectangle', 100, 50, { adjust: { corner: 8 } });
  const large = shapeCommands('rounded_rectangle', 400, 200, { adjust: { corner: 8 } });

  // Act
  const smallStart = small[0];
  const largeStart = large[0];

  // Assert
  assert.deepEqual(smallStart, ['M', 8, 0]);
  assert.deepEqual(largeStart, ['M', 8, 0]);
});

test('an unadjusted rounded rectangle takes OOXML default proportion of its shorter side', () => {
  // Act
  const [start] = shapeCommands('rounded_rectangle', 300, 60);

  // Assert
  assert.equal(Math.round(start[1] * 100) / 100, 10);
});

test('a chevron points right by default and its point is a length', () => {
  // Act
  const tip = points(shapeCommands('chevron', 180, 60, { adjust: { point: 16 } }));

  // Assert
  assert.deepEqual(tip, [[0, 0], [164, 0], [180, 30], [164, 60], [0, 60], [16, 30]]);
});

test('an arrow pointing down has its tip at the bottom centre of the frame', () => {
  // Act
  const tip = points(shapeCommands('arrow', 40, 100, { direction: 'down' }))[3];

  // Assert
  assert.deepEqual(tip, [20, 100]);
});

test('a triangle points up unless told otherwise', () => {
  // Act
  const up = points(shapeCommands('triangle', 100, 80));
  const right = points(shapeCommands('triangle', 100, 80, { direction: 'right' }));

  // Assert
  assert.deepEqual(up[1], [50, 0]);
  assert.deepEqual(right[1], [100, 40]);
});

test('a brace pointing right opens to the left and reaches the right edge at its middle', () => {
  // Act
  const commands = shapeCommands('brace', 20, 280, { direction: 'right' });

  // Assert
  assert.deepEqual(commands[0], ['M', 0, 0]);
  assert.deepEqual(commands[3].slice(-2), [20, 140]);
  assert.ok(isOpen('brace'));
});

test('a callout wedge leaves the edge facing its tail and reaches the tail', () => {
  // Act
  const below = points(shapeCommands('callout', 200, 56, { tail: [-60, 90] }));
  const right = points(shapeCommands('callout', 260, 50, { tail: [340, 60] }));

  // Assert
  assert.ok(below.some(([x, y]) => x === -60 && y === 90));
  assert.ok(below.filter(([, y]) => y === 56).length >= 3);
  assert.ok(right.some(([x, y]) => x === 340 && y === 60));
  assert.ok(right.filter(([x]) => x === 260).length >= 3);
});

test('a callout whose tail sits inside its balloon draws no wedge', () => {
  // Act
  const balloon = points(shapeCommands('callout', 200, 56, { tail: [100, 20] }));

  // Assert
  assert.equal(balloon.length, 4);
});

test('an ellipse path keeps whole arc flags', () => {
  // Act
  const d = shapePath('ellipse', 200, 120);

  // Assert
  assert.equal(d, 'M0 60 A100 60 0 1 0 200 60 A100 60 0 1 0 0 60 Z');
});

test('dashes scale with the stroke width and solid has none', () => {
  // Act
  const dash = dashArray('dash', 1.5);
  const solid = dashArray('solid', 1.5);

  // Assert
  assert.equal(dash, '6 4.5');
  assert.equal(solid, null);
});