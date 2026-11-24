const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { families } = require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'headless.js'));

test('families collects unique sorted fonts from slides, variants, charts, text marks, runs, and tables', () => {
  // Arrange
  const deck = {
    slides: [
      {
        id: 'one',
        items: [
          {
            text: {
              paragraphs: [
                {
                  mark: { font: 'Georgia' },
                  runs: [{ font: 'Arial' }, { font: 'Georgia' }, {}]
                }
              ]
            }
          },
          {
            chart: { text: { font: 'Inter' } }
          },
          {
            table: {
              cells: [
                {
                  text: {
                    paragraphs: [{ mark: { font: 'Courier New' }, runs: [{ font: 'Arial' }] }]
                  }
                }
              ]
            }
          }
        ]
      }
    ],
    variants: [
      {
        id: 'one-alt',
        items: [
          {
            text: {
              paragraphs: [{ mark: { font: 'Verdana' }, runs: [] }]
            }
          }
        ]
      }
    ]
  };

  // Act
  const result = families(deck);

  // Assert
  assert.deepEqual(result, ['Arial', 'Courier New', 'Georgia', 'Inter', 'Verdana']);
});

test('families handles empty slides and items without text or charts', () => {
  // Arrange
  const deck = {
    slides: [{ id: 'blank', items: [{ picture: { src: 'logo.png' } }] }],
    variants: []
  };

  // Act
  const result = families(deck);

  // Assert
  assert.deepEqual(result, []);
});
