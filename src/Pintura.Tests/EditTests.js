const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { snapTargets, moveBox, resizeBox, movePoint, moveLine, valueEdits } = require(path.join(__dirname, '..', 'Pintura',
  'wwwroot', 'edit.js'));

const slide = {
  guides: [{ kind: 'area', name: 'left', rect: [40, 100, 430, 380] }],
  items: [
    { id: 'title', frame: [40, 24, 880, 56] },
    { id: 'box', frame: [500, 300, 100, 50] },
    { id: 'arrow', from: [600, 200], to: [700, 200] }
  ]
};

test('a box dragged near an area edge snaps its own edge onto it', () => {
  // Arrange
  const targets = snapTargets(slide, [960, 540], 1);

  // Act
  const { box, lines } = moveBox([500, 300, 100, 50], -455, 3, targets, 6);

  // Assert
  assert.deepEqual(box, [40, 303, 100, 50]);
  assert.deepEqual(lines, [{ axis: 'x', at: 40 }]);
});

test('a box as wide as the one it lines up under shows a line at each edge and the middle', () => {
  // Arrange
  const heading = { id: 'heading', frame: [674.18, 140, 339.64, 24] };
  const bullets = { id: 'bullets', frame: [700.18, 178, 339.64, 300] };
  const targets = snapTargets({ items: [heading, bullets] }, [1088, 612], 1);

  // Act
  const { box, lines } = moveBox(bullets.frame, -24, 0, targets, 4);

  // Assert
  assert.ok(Math.abs(box[0] - 674.18) < 1e-9);
  assert.deepEqual(box.slice(1), [178, 339.64, 300]);
  assert.deepEqual(lines.map((line) => [line.axis, Math.round(line.at * 100) / 100]),
    [['x', 674.18], ['x', 844], ['x', 1013.82]]);
});

test('a box snaps by its middle to the middle of the slide', () => {
  // Arrange
  const targets = snapTargets(slide, [960, 540], 1);

  // Act
  const { box } = moveBox([500, 300, 100, 50], -72, 0, targets, 6);

  // Assert
  assert.equal(box[0] + box[2] / 2, 480);
});

test('the moving item is not a target for itself', () => {
  // Arrange
  const targets = snapTargets(slide, [960, 540], 1);

  // Act
  const { box, lines } = moveBox([500, 300, 100, 50], 2, 2, targets, 1);

  // Assert
  assert.deepEqual(box, [502, 302, 100, 50]);
  assert.deepEqual(lines, []);
});

test('without targets a box moves by the drag to the nearest whole point, keeping its size', () => {
  // Act
  const { box, lines } = moveBox([500.25, 300, 100.5, 50], 1.5, -2.4, null, 6);

  // Assert
  assert.deepEqual(box, [502, 298, 100.5, 50]);
  assert.deepEqual(lines, []);
});

test('resizing from a corner moves only its two edges and snaps them', () => {
  // Arrange
  const targets = { x: [470], y: [480] };

  // Act
  const { box } = resizeBox([40, 100, 400, 300], 'se', 28, 78, targets, 6, false);

  // Assert
  assert.deepEqual(box, [40, 100, 430, 380]);
});

test('resizing from the left edge keeps the right edge where it is', () => {
  // Act
  const { box } = resizeBox([100, 100, 200, 100], 'w', 50, 20, null, 6, false);

  // Assert
  assert.deepEqual(box, [150, 100, 150, 100]);
});

test('resizing moves the dragged edge to a whole point and leaves the others where they were', () => {
  // Act
  const { box } = resizeBox([674.18, 140, 339.64, 24], 'e', 10.3, 0, null, 6, false);

  // Assert
  assert.ok(Math.abs(box[0] + box[2] - 1024) < 1e-9);
  assert.deepEqual([box[0], box[1], box[3]], [674.18, 140, 24]);
});

test('a box never resizes below a point, whichever edge is dragged past the other', () => {
  // Act
  const { box } = resizeBox([100, 100, 200, 100], 'w', 500, 0, null, 6, false);

  // Assert
  assert.deepEqual(box, [299, 100, 1, 100]);
});

test('with the aspect kept, a corner resize scales both sides by the larger change', () => {
  // Act
  const { box } = resizeBox([0, 0, 200, 100], 'nw', -100, -10, null, 6, true);

  // Assert
  assert.deepEqual(box, [-100, -50, 300, 150]);
});

test('a line end snaps each axis on its own', () => {
  // Act
  const { point } = movePoint([600, 200], 38, 97, { x: [640], y: [300] }, 6);

  // Assert
  assert.deepEqual(point, [640, 300]);
});

test('a whole line moves both ends by the same snapped shift', () => {
  // Act
  const { from, to } = moveLine([700, 200], [600, 200], -158, 0, { x: [440], y: [] }, 6);

  // Assert
  assert.deepEqual(from, [540, 200]);
  assert.deepEqual(to, [440, 200]);
});
test('a box snapped under one an area laid out writes the edge it snapped to, not a rounding of it', () => {
  // Arrange
  const item = {
    frame: [700.18, 178, 339.64, 300],
    edit: { frame: { path: 'elements.bullets.frame', source: '[700.18, 178, 339.64, 300]' } }
  };

  // Act
  const edits = valueEdits(item, { frame: [674.18 + 1e-12, 178, 339.64, 300] });

  // Assert
  assert.deepEqual(edits.map((edit) => edit.numbers), [[674.18, 178, 339.64, 300]]);
});
