const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { changeMarks, changedStep, chartParts, shownDeck, textChanges, textDiff, textMarks, versionDay, versionTime } =
  require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'compare.js'));

function words(...paragraphs) {
  return { paragraphs: paragraphs.map((runs) => ({ runs: [].concat(runs).map((text) => ({ text })) })) };
}

const earlier = {
  items: [
    { id: 'title', owner: 'slide', frame: [40, 24, 880, 56] },
    { id: 'box', owner: 'slide', frame: [40, 200, 100, 50] },
    { id: 'note', owner: 'slide', frame: [40, 300, 200, 50], text: words('$4.4B operating income') },
    { id: 'old', owner: 'slide', frame: [300, 300, 60, 60] }
  ]
};

const later = {
  items: [
    { id: 'title', owner: 'slide', frame: [40, 24, 880, 56] },
    { id: 'new', owner: 'slide', from: [600, 250], to: [500, 200] },
    { id: 'box', owner: 'slide', frame: [52, 200, 100, 50] },
    { id: 'note', owner: 'slide', frame: [40, 300, 200, 50], text: words('$4.3B operating income') }
  ]
};

const diff = {
  items: [
    { id: 'new', owner: 'slide', status: 'added', changes: [] },
    { id: 'box', owner: 'slide', status: 'changed', changes: [{ key: 'frame' }] },
    { id: 'note', owner: 'slide', status: 'changed', changes: [{ key: 'text' }] },
    { id: 'title', owner: 'slide', status: 'changed', changes: [{ key: 'size', cause: 'theme/styles.yaml size' }] },
    { id: 'old', owner: 'slide', status: 'removed', changes: [] }
  ]
};

test('a reworded word is removed and added, and the words around it are kept', () => {
  // Act
  const parts = textDiff('$4.4B operating income', '$4.3B operating income');

  // Assert
  assert.deepEqual(parts, [
    { op: 'removed', text: '$4.4B' },
    { op: 'added', text: '$4.3B' },
    { op: 'same', text: ' operating income' }
  ]);
});

test('the space between two changed words joins them into one change', () => {
  // Act
  const parts = textDiff('up from 3.9 to 11.4 today', 'up from 4.0 to 11.5 today');

  // Assert
  assert.deepEqual(parts, [
    { op: 'same', text: 'up from ' },
    { op: 'removed', text: '3.9' },
    { op: 'added', text: '4.0' },
    { op: 'same', text: ' to ' },
    { op: 'removed', text: '11.4' },
    { op: 'added', text: '11.5' },
    { op: 'same', text: ' today' }
  ]);
  assert.deepEqual(textDiff('grew a lot fast', 'fell far fast'), [
    { op: 'removed', text: 'grew a lot' },
    { op: 'added', text: 'fell far' },
    { op: 'same', text: ' fast' }
  ]);
});

test('each side marks the words only it has, and a caret where the other side has words it lacks', () => {
  // Arrange
  const before = 'Revenue grew fast in 2025';
  const after = 'Revenue grew in 2026';

  // Act
  const shownAfter = textMarks(before, after, false);
  const shownBefore = textMarks(before, after, true);

  // Assert
  assert.deepEqual(shownAfter, [{ start: 12, end: 12 }, { start: 16, end: 20 }]);
  assert.deepEqual(shownBefore, [{ start: 13, end: 17 }, { start: 21, end: 25 }]);
});

test('a reworded element is marked in its words rather than outlined, and a moved one is outlined', () => {
  // Act
  const marks = changeMarks(diff, earlier, later, false);

  // Assert
  assert.deepEqual(marks.find((mark) => mark.id === 'note'),
    { id: 'note', owner: 'slide', status: 'changed', index: 3, outline: false, restyled: false });
  assert.deepEqual(marks.find((mark) => mark.id === 'box'),
    { id: 'box', owner: 'slide', status: 'changed', index: 2, outline: true, restyled: false });
});

test('an element restyled by the theme is outlined as restyled', () => {
  // Act
  const marks = changeMarks(diff, earlier, later, false);

  // Assert
  assert.deepEqual(marks.find((mark) => mark.id === 'title'),
    { id: 'title', owner: 'slide', status: 'changed', index: 0, outline: true, restyled: true });
});

test('an element is marked only on the side that draws it', () => {
  // Act
  const after = changeMarks(diff, earlier, later, false).map((mark) => mark.id);
  const before = changeMarks(diff, earlier, later, true).map((mark) => mark.id);

  // Assert
  assert.deepEqual(after, ['new', 'box', 'note', 'title']);
  assert.deepEqual(before, ['box', 'note', 'title', 'old']);
});

test("a text's changes are located by paragraph, and a table's by cell", () => {
  // Arrange
  const cell = (row, column, text) => ({ row, column, text: words(text) });
  const table = (second) => ({ table: { cells: [cell(1, 1, 'Revenue'), cell(1, 2, second)] } });

  // Act
  const text = textChanges({ text: words('Up', 'from 3.9') }, { text: words('Up', 'from 4.0') }, false);
  const cells = textChanges(table('11.4'), table('11.5'), false);

  // Assert
  assert.deepEqual(text, [{ cell: null, marks: [{ start: 8, end: 11 }] }]);
  assert.deepEqual(cells, [{ cell: { row: 1, column: 2 }, marks: [{ start: 0, end: 4 }] }]);
});

test('a word made bold is marked as restyled in its own characters, and the words around it are not', () => {
  // Arrange
  const plain = { text: words(['Revenue up', ' from 3.9']) };
  const bolded = { text: { paragraphs: [{ runs: [{ text: 'Revenue', bold: true }, { text: ' up from 3.9' }] }] } };

  // Act
  const shownAfter = textChanges(plain, bolded, false);
  const shownBefore = textChanges(plain, bolded, true);

  // Assert
  assert.deepEqual(shownAfter, [{ cell: null, marks: [{ start: 0, end: 7, restyled: true }] }]);
  assert.deepEqual(shownBefore, shownAfter);
});

test('formatting changed beside a reworded word is marked as well, in the offsets of the side showing', () => {
  // Arrange
  const before = { text: { paragraphs: [{ runs: [{ text: 'Up 3.9 from ' }, { text: '2024' }] }] } };
  const after = { text: { paragraphs: [{ runs: [{ text: 'Up 4.25 from ' }, { text: '2024', italic: true }] }] } };

  // Act
  const marks = textChanges(before, after, false)[0].marks;

  // Assert
  assert.deepEqual(marks, [{ start: 3, end: 7 }, { start: 13, end: 17, restyled: true }]);
});

test('an element whose runs alone were restyled is marked in its words rather than outlined', () => {
  // Arrange
  const restyled = {
    items: [{ id: 'note', owner: 'slide', status: 'changed',
      changes: [{ key: 'paragraph 2 run 1.bold' }, { key: 'cell 1,2 paragraph 1 run 3.italic' }] }]
  };

  // Act
  const marks = changeMarks(restyled, earlier, later, false);

  // Assert
  assert.equal(marks[0].outline, false);
});


test('stepping through changed slides skips the unchanged ones, both ways', () => {
  // Arrange
  const ids = ['one', 'two', 'three', 'four', 'five'];
  const changed = new Set(['two', 'four']);

  // Act
  const forward = changedStep(ids, changed, 'two', 1);
  const backward = changedStep(ids, changed, 'four', -1);
  const fromUnchanged = changedStep(ids, changed, 'three', 1);

  // Assert
  assert.equal(forward, 'four');
  assert.equal(backward, 'two');
  assert.equal(fromUnchanged, 'four');
});

test('stepping past the last changed slide stays put, and a removed slide steps into the deck', () => {
  // Arrange
  const ids = ['one', 'two', 'three'];
  const changed = new Set(['one', 'three']);

  // Act
  const end = changedStep(ids, changed, 'three', 1);
  const start = changedStep(ids, changed, 'one', -1);
  const fromRemoved = changedStep(ids, changed, 'gone', 1);

  // Assert
  assert.equal(end, 'three');
  assert.equal(start, 'one');
  assert.equal(fromRemoved, 'one');
});

test('a version is placed by its day as near ones are spoken of, and its time', () => {
  // Arrange
  const now = new Date(2026, 9, 7, 16, 30);
  const dates = [new Date(2026, 9, 7, 9, 5), new Date(2026, 9, 6, 23, 59), new Date(2026, 9, 2, 14, 0),
    new Date(2026, 8, 30, 8, 0), new Date(2025, 11, 31, 18, 45)];

  // Act
  const names = dates.map((date) => `${versionDay(date, now)} ${versionTime(date)}`);

  // Assert
  assert.deepEqual(names, ['Today 9:05 AM', 'Yesterday 11:59 PM', 'Friday 2:00 PM', 'Sep 30 8:00 AM',
    'Dec 31, 2025 6:45 PM']);
});

const columns = { family: 'column', categories: ['2024', '2025'], series: [{ name: 'Space' }, { name: 'AI' }] };

test("a chart's changes are marked in the parts that draw them", () => {
  // Arrange
  const changes = [
    { key: 'title', before: 'EBITDA', after: 'Segment EBITDA' },
    { key: 'series.AI.points.2025.data_labels.bold', before: 'false', after: 'true' },
    { key: 'series.AI.points.2025.data_labels.color', before: '#111111', after: '#222222' },
    { key: 'series.Space.points.2024.color', after: 'accent6 (#70AD47)' },
    { key: 'series Space', before: '3796, 4086', after: '3796, 4200' },
    { key: 'series_colors', before: '#111111, #222222', after: '#111111, #333333' },
    { key: 'value_axis.format', before: 'millions', after: 'billions' }
  ];

  // Act
  const parts = chartParts(changes, columns);

  // Assert
  assert.deepEqual(parts, [
    { parts: ['title'] },
    { parts: ['label'], series: 1, point: 1 },
    { parts: ['label'], series: 1, point: 1 },
    { parts: ['bar', 'marker', 'slice'], series: 0, point: 0 },
    { parts: ['bar', 'marker', 'slice', 'label'], series: 0, point: 1 },
    { parts: ['line'], series: 0 },
    { parts: ['bar', 'marker', 'line', 'legend'], series: 1 },
    { parts: ['value_axis'] }
  ]);
});

test("a pie's colors are its slices', and a change drawn across the whole chart outlines it", () => {
  // Arrange
  const pie = { ...columns, family: 'pie' };
  const colors = [{ key: 'series_colors', before: '#111111, #222222', after: '#444444, #222222' }];

  // Act
  const slices = chartParts(colors, pie);
  const whole = chartParts([...colors, { key: 'type', before: 'pie', after: 'doughnut' }], pie);

  // Assert
  assert.deepEqual(slices, [{ parts: ['slice', 'legend'], point: 0 }]);
  assert.equal(whole, null);
});

test('a changed chart is marked in its parts rather than outlined, unless a change spans it', () => {
  // Arrange
  const chart = (title) => ({ items: [{ id: 'chart', owner: 'slide', chart: { ...columns, title } }] });
  const changed = (key) => ({ items: [{ id: 'chart', owner: 'slide', status: 'changed', changes: [{ key }] }] });

  // Act
  const [titled] = changeMarks(changed('title'), chart('A'), chart('B'), false);
  const [moved] = changeMarks(changed('frame'), chart('A'), chart('B'), false);

  // Assert
  assert.deepEqual([titled.outline, titled.parts], [false, [{ parts: ['title'] }]]);
  assert.deepEqual([moved.outline, moved.parts], [true, null]);
});

test("a changed table is marked in the cells whose look changed rather than outlined, unless a change spans it", () => {
  // Arrange
  const table = { items: [{ id: 'table', owner: 'slide', table: { cells: [] } }] };
  const changed = (...keys) => ({ items: [{ id: 'table', owner: 'slide', status: 'changed',
    changes: keys.map((key) => ({ key })) }] });

  // Act
  const [filled] = changeMarks(changed('cell 3,3.fill', 'cell 3,3.bold', 'cell 4,2.fill', 'cell 2,1'), table, table,
    false);
  const [reworded] = changeMarks(changed('cell 2,1', 'cell 2,1 paragraph 1 run 1.italic'), table, table, false);
  const [moved] = changeMarks(changed('cell 3,3.fill', 'frame'), table, table, false);

  // Assert
  assert.deepEqual([filled.outline, filled.cells], [false, [{ row: 3, column: 3 }, { row: 4, column: 2 }]]);
  assert.deepEqual([reworded.outline, reworded.cells], [false, []]);
  assert.deepEqual([moved.outline, moved.cells], [true, null]);
});

test("a version stepped to keeps the working tree's comments, which an agent marks after its round", () => {
  // Arrange
  const deck = { slides: [{ id: 'now' }], comments: [{ id: 'c1', slide: 'now', done: 'reworded' }] };
  const after = { slides: [{ id: 'then' }], comments: [{ id: 'c1', slide: 'now' }] };

  // Act
  const shown = shownDeck(deck, after);

  // Assert
  assert.deepEqual(shown.slides, after.slides);
  assert.deepEqual(shown.comments, deck.comments);
  assert.equal(shownDeck(deck, null), deck);
});
