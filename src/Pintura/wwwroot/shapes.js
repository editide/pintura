'use strict';

// SVG geometry for the sixteen shapes, in points within the frame. Adjustments are lengths; left out, they take
// OOXML's default proportion of the frame's shorter side, so an unadjusted shape matches PowerPoint's preset.

function round(value) {
  return Math.round(value * 1000) / 1000;
}

function clamp(value, low, high) {
  return Math.min(Math.max(value, low), high);
}

function path(commands) {
  return commands.map(([command, ...points]) => command + points.map(round).join(' ')).join(' ');
}

// Directional shapes are drawn pointing along u, across v, then turned to face their direction.
function orient(commands, direction, width, height) {
  const map = {
    right: (u, v) => [u, v],
    left: (u, v) => [width - u, v],
    down: (u, v) => [v, u],
    up: (u, v) => [v, height - u]
  }[direction];
  return commands.map(([command, ...points]) => {
    const mapped = [];
    for (let index = 0; index < points.length; index += 2) {
      mapped.push(...map(points[index], points[index + 1]));
    }
    return [command, ...mapped];
  });
}

function directional(shape, length, thickness, adjust) {
  const shorter = Math.min(length, thickness);
  switch (shape) {
    case 'triangle':
      return [['M', 0, 0], ['L', length, thickness / 2], ['L', 0, thickness], ['Z']];
    case 'right_triangle':
      return [['M', length, 0], ['L', 0, 0], ['L', 0, thickness], ['Z']];
    case 'chevron': {
      const point = clamp(adjust.point ?? shorter * 0.5, 0, length);
      return [['M', 0, 0], ['L', length - point, 0], ['L', length, thickness / 2], ['L', length - point, thickness],
        ['L', 0, thickness], ['L', point, thickness / 2], ['Z']];
    }
    case 'home_plate': {
      const point = clamp(adjust.point ?? shorter * 0.5, 0, length);
      return [['M', 0, 0], ['L', length - point, 0], ['L', length, thickness / 2], ['L', length - point, thickness],
        ['L', 0, thickness], ['Z']];
    }
    case 'arrow': {
      const point = clamp(adjust.point ?? shorter * 0.5, 0, length);
      const top = thickness / 4;
      const bottom = thickness * 3 / 4;
      return [['M', 0, top], ['L', length - point, top], ['L', length - point, 0], ['L', length, thickness / 2],
        ['L', length - point, thickness], ['L', length - point, bottom], ['L', 0, bottom], ['Z']];
    }
    case 'brace': {
      const middle = length / 2;
      const curl = Math.min(thickness / 4, middle);
      const center = thickness / 2;
      return [['M', 0, 0], ['Q', middle, 0, middle, curl], ['L', middle, center - curl],
        ['Q', middle, center, length, center], ['Q', middle, center, middle, center + curl],
        ['L', middle, thickness - curl], ['Q', middle, thickness, 0, thickness]];
    }
    case 'bracket': {
      const curl = Math.min(thickness / 4, length);
      return [['M', 0, 0], ['Q', length, 0, length, curl], ['L', length, thickness - curl],
        ['Q', length, thickness, 0, thickness]];
    }
    default:
      throw new Error(`not a directional shape: ${shape}`);
  }
}

const DEFAULT_DIRECTIONS = { triangle: 'up', right_triangle: 'up' };

// The callout's balloon is the frame, and its wedge leaves the edge facing the tail, a point in frame coordinates.
function callout(width, height, tail) {
  const [x, y] = tail;
  if (x >= 0 && x <= width && y >= 0 && y <= height) {
    return [['M', 0, 0], ['L', width, 0], ['L', width, height], ['L', 0, height], ['Z']];
  }
  const across = (x - width / 2) / (width / 2 || 1);
  const down = (y - height / 2) / (height / 2 || 1);
  const vertical = Math.abs(down) >= Math.abs(across);
  const edge = vertical ? width : height;
  const base = clamp(edge * 0.2, Math.min(8, edge / 2), Math.min(40, edge / 2));
  const center = clamp(vertical ? x : y, edge * 0.15 + base / 2, edge * 0.85 - base / 2);
  const low = center - base / 2;
  const high = center + base / 2;
  if (vertical && down > 0) {
    return [['M', 0, 0], ['L', width, 0], ['L', width, height], ['L', high, height], ['L', x, y],
      ['L', low, height], ['L', 0, height], ['Z']];
  }
  if (vertical) {
    return [['M', 0, 0], ['L', low, 0], ['L', x, y], ['L', high, 0], ['L', width, 0], ['L', width, height],
      ['L', 0, height], ['Z']];
  }
  if (across > 0) {
    return [['M', 0, 0], ['L', width, 0], ['L', width, low], ['L', x, y], ['L', width, high], ['L', width, height],
      ['L', 0, height], ['Z']];
  }
  return [['M', 0, 0], ['L', width, 0], ['L', width, height], ['L', 0, height], ['L', 0, high], ['L', x, y],
    ['L', 0, low], ['Z']];
}

// options: { direction, adjust, tail }, with tail already relative to the frame's top-left corner.
function shapeCommands(shape, width, height, options = {}) {
  const adjust = options.adjust || {};
  const shorter = Math.min(width, height);
  switch (shape) {
    case 'rectangle':
      return [['M', 0, 0], ['L', width, 0], ['L', width, height], ['L', 0, height], ['Z']];
    case 'rounded_rectangle': {
      const r = clamp(adjust.corner ?? shorter * 0.16667, 0, shorter / 2);
      return [['M', r, 0], ['L', width - r, 0], ['Q', width, 0, width, r], ['L', width, height - r],
        ['Q', width, height, width - r, height], ['L', r, height], ['Q', 0, height, 0, height - r], ['L', 0, r],
        ['Q', 0, 0, r, 0], ['Z']];
    }
    case 'ellipse':
      return [['M', 0, height / 2], ['A', width / 2, height / 2, 0, 1, 0, width, height / 2],
        ['A', width / 2, height / 2, 0, 1, 0, 0, height / 2], ['Z']];
    case 'diamond':
      return [['M', width / 2, 0], ['L', width, height / 2], ['L', width / 2, height], ['L', 0, height / 2], ['Z']];
    case 'parallelogram': {
      const slant = clamp(adjust.slant ?? shorter * 0.25, 0, width);
      return [['M', slant, 0], ['L', width, 0], ['L', width - slant, height], ['L', 0, height], ['Z']];
    }
    case 'trapezoid': {
      const slant = clamp(adjust.slant ?? shorter * 0.25, 0, width / 2);
      return [['M', 0, height], ['L', slant, 0], ['L', width - slant, 0], ['L', width, height], ['Z']];
    }
    case 'pentagon':
      return [['M', width / 2, 0], ['L', width, height * 0.38197], ['L', width * 0.80902, height],
        ['L', width * 0.19098, height], ['L', 0, height * 0.38197], ['Z']];
    case 'hexagon': {
      const inset = clamp(shorter * 0.25, 0, width / 2);
      return [['M', inset, 0], ['L', width - inset, 0], ['L', width, height / 2], ['L', width - inset, height],
        ['L', inset, height], ['L', 0, height / 2], ['Z']];
    }
    case 'callout':
      return callout(width, height, options.tail || [width / 2, height / 2]);
    default: {
      const direction = options.direction || DEFAULT_DIRECTIONS[shape] || 'right';
      const along = direction === 'right' || direction === 'left';
      const commands = directional(shape, along ? width : height, along ? height : width, adjust);
      return orient(commands, direction, width, height);
    }
  }
}

// Arc flags must stay whole numbers, so arcs are written apart from the rounded coordinates.
function shapePath(shape, width, height, options = {}) {
  return shapeCommands(shape, width, height, options)
    .map(([command, ...points]) => command === 'A'
      ? `A${round(points[0])} ${round(points[1])} 0 ${points[3]} ${points[4]} ${round(points[5])} ${round(points[6])}`
      : path([[command, ...points]]))
    .join(' ');
}

// Braces and brackets are open strokes; filling one would close it with a straight edge.
function isOpen(shape) {
  return shape === 'brace' || shape === 'bracket';
}

// Arrowheads as SVG markers in a 10 × 10 box, drawn three line-widths long as PowerPoint's medium size is. The anchor
// sits on the line's end, and the shaft stops short of a pointed tip, which is narrower than the line near its end.
const MARKERS = {
  arrow: { path: 'M0 0 L10 5 L0 10', fill: false, anchor: 10, shaft: 10 },
  triangle: { path: 'M0 0 L10 5 L0 10 Z', fill: true, anchor: 10, shaft: 5 },
  stealth: { path: 'M0 0 L10 5 L0 10 L3 5 Z', fill: true, anchor: 10, shaft: 5 },
  diamond: { path: 'M0 5 L5 0 L10 5 L5 10 Z', fill: true, anchor: 5, shaft: 5 },
  oval: { path: 'M0 5 A5 5 0 1 0 10 5 A5 5 0 1 0 0 5 Z', fill: true, anchor: 5, shaft: 5 }
};

function dashArray(dash, width) {
  if (dash === 'dash') {
    return `${round(width * 4)} ${round(width * 3)}`;
  }
  if (dash === 'dot') {
    return `${round(width)} ${round(width * 2)}`;
  }
  return null;
}

if (typeof module !== 'undefined') {
  module.exports = { shapePath, shapeCommands, isOpen, MARKERS, dashArray, clamp };
}