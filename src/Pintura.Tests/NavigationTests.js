const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { wheelStep } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'navigation.js'));

function page() {
  return {
    state: { deck: { slides: [{ id: 'one' }, { id: 'two' }] }, view: 'slide', presenting: false },
    editing: { text: null, gesture: null },
    container: { scrollHeight: 500, clientHeight: 500 }
  };
}

function wheel(overrides = {}) {
  return { deltaX: 0, deltaY: 100, target: { closest: () => null }, ...overrides };
}

test('wheel down advances and wheel up goes back', () => {
  // Arrange
  const { state, editing, container } = page();

  // Act
  const down = wheelStep(wheel(), state, editing, container);
  const up = wheelStep(wheel({ deltaY: -3, deltaMode: 1 }), state, editing, container);

  // Assert
  assert.deepEqual([down, up], [1, -1]);
});

test('horizontal scrolling, zooming, and text editing do not navigate', () => {
  // Arrange
  const { state, editing, container } = page();
  const ignored = [
    { deltaY: 0 }, { deltaX: 200 }, { ctrlKey: true }, { metaKey: true }, { altKey: true },
    { target: { closest: () => ({}) } }
  ];

  // Act
  const steps = ignored.map((overrides) => wheelStep(wheel(overrides), state, editing, container));
  steps.push(wheelStep(wheel(), state, { ...editing, text: {} }, container));
  steps.push(wheelStep(wheel(), state, { ...editing, gesture: {} }, container));

  // Assert
  assert.ok(steps.every((step) => step === 0));
});

test('navigation waits for the deck and leaves the grid scrolling normally', () => {
  // Arrange
  const { state, editing, container } = page();

  // Act
  const loading = wheelStep(wheel(), { ...state, deck: null }, editing, container);
  const grid = wheelStep(wheel(), { ...state, view: 'grid' }, editing, container);
  const present = wheelStep(wheel(), { ...state, view: 'grid', presenting: true }, editing, container);

  // Assert
  assert.deepEqual([loading, grid, present], [0, 0, 1]);
});

test('a slide zoomed taller than its view scrolls instead of navigating', () => {
  // Arrange
  const { state, editing } = page();

  // Act
  const step = wheelStep(wheel(), state, editing, { scrollHeight: 900, clientHeight: 500 });

  // Assert
  assert.equal(step, 0);
});