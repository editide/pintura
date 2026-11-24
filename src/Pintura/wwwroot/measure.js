'use strict';

// Headless-only measurements over a drawn stage: every text box, picture margins, chart labels, and text contrast.

// Every text box on a drawn stage.
function measureText(stage) {
  return [...stage.querySelectorAll('.item')].map(measureBox).filter(Boolean);
}

// Where each picture on a drawn stage draws its image, and the transparent margins built into the image, for its
// edges to be checked where they show. A crop is the margins' fix already, so a cropped picture has none measured.
async function measurePictures(stage) {
  return Promise.all([...stage.querySelectorAll('.item.picture-item')].map(async (node) => {
    const { picture, frame, fit, crop } = node.picture;
    const [x, y, width, height] = frame;
    try {
      const size = await imageSize(picture);
      const shown = crop ? [size[0] - crop[0] - crop[2], size[1] - crop[1] - crop[3]] : size;
      const [left, top, drawnWidth, drawnHeight] = fitted(fit, width, height, shown);
      return {
        id: node.dataset.id,
        owner: node.dataset.owner,
        drawn: [x + left, y + top, drawnWidth, drawnHeight].map((value) => Math.round(value * 10) / 10),
        margins: crop ? null : await imageMargins(picture, size)
      };
    } catch {
      return null;
    }
  })).then((pictures) => pictures.filter(Boolean));
}

// The image drawn at up to 512 pixels on its long side, enough to place its ink to a fraction of a percent.
async function imageMargins(url, [width, height]) {
  const image = new Image();
  image.src = url;
  await image.decode();
  const scale = 512 / Math.max(width, height);
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(width * scale));
  canvas.height = Math.max(1, Math.round(height * scale));
  const context = canvas.getContext('2d', { willReadFrequently: true });
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  return inkMargins(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
}

// The transparent margins around what an RGBA image draws, as fractions [left, top, right, bottom] of its width and
// height, or null for a blank one. Near-transparent antialiasing doesn't count as ink.
function inkMargins(data, width, height) {
  let [left, top, right, bottom] = [width, height, -1, -1];
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (data[(y * width + x) * 4 + 3] > 8) {
        left = Math.min(left, x);
        right = Math.max(right, x);
        top = Math.min(top, y);
        bottom = Math.max(bottom, y);
      }
    }
  }
  if (right < 0) {
    return null;
  }
  const round = (value) => Math.round(value * 1000) / 1000;
  return [round(left / width), round(top / height), round((width - 1 - right) / width),
    round((height - 1 - bottom) / height)];
}

// Every chart's labels on a drawn stage, for text laid over a chart to be checked against them.
function measureCharts(stage) {
  return [...stage.querySelectorAll('.item.chart')].map((node) => ({
    id: node.dataset.id,
    labels: [...node.querySelectorAll('text')].filter((text) => text.textContent)
      .map((text) => ({ text: text.textContent, rect: onSlide(text.getBoundingClientRect(), stage),
        clipped: 'clipped' in text.dataset, moved: text.dataset.moved ?? null,
        part: text.dataset.part ?? null }))
  }));
}

// Each run of text on a drawn stage, in the color it's drawn in, over the color painted beneath its middle: a chart's
// bar under its label, a shape under its text, or the slide. Text over a picture or a gradient has no one color behind
// it, so it's left out, as are bullets, which are marks rather than text to read.
function measureContrast(stage) {
  const painted = [...stage.querySelectorAll('path, rect, circle, ellipse, polygon, td')];
  const walker = document.createTreeWalker(stage, NodeFilter.SHOW_TEXT);
  const found = new Map();
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const parent = node.parentElement;
    const item = parent.closest('.item');
    if (!item || !node.textContent.trim() || parent.closest('.bullet, .area-item')) {
      continue;
    }
    const svgText = parent instanceof SVGElement;
    const style = getComputedStyle(parent);
    const color = parseColor(svgText ? style.fill : style.color);
    if (!color || color[3] === 0) {
      continue;
    }
    const range = document.createRange();
    range.selectNodeContents(node);
    const rect = [...range.getClientRects()].find((line) => line.width > 0 && line.height > 0);
    if (!rect) {
      continue;
    }
    const behind = backdrop(rect.left + rect.width / 2, rect.top + rect.height / 2, node, painted, stage);
    if (!behind) {
      continue;
    }
    const entry = {
      id: item.dataset.id,
      owner: item.dataset.owner,
      text: node.textContent.trim(),
      color: hex(color),
      backdrop: hex(behind),
      size: Math.round(parseFloat(style.fontSize) * 10) / 10,
      bold: Number(style.fontWeight) >= 700,
      part: svgText ? parent.dataset.part ?? null : null
    };
    const key = `${entry.id}|${entry.color}|${entry.backdrop}|${entry.size}|${entry.bold}`;
    if (!found.has(key)) {
      found.set(key, entry);
    }
  }
  return [...found.values()];
}

// The color painted at a point beneath a text node: the latest fill before it in the document that covers the point,
// blended over what's beneath when it's see-through. Null over a picture or a fill that isn't one color.
function backdrop(x, y, text, painted, stage) {
  const under = painted.filter((node) => node.compareDocumentPosition(text) & Node.DOCUMENT_POSITION_FOLLOWING);
  const pictures = [...stage.querySelectorAll('img')]
    .filter((image) => image.compareDocumentPosition(text) & Node.DOCUMENT_POSITION_FOLLOWING);
  const covers = (node) => {
    const box = node.getBoundingClientRect();
    if (x < box.left || x > box.right || y < box.top || y > box.bottom) {
      return false;
    }
    if (!(node instanceof SVGGeometryElement)) {
      return true;
    }
    const point = new DOMPoint(x, y).matrixTransform(node.getScreenCTM().inverse());
    return node.isPointInFill(point);
  };
  const layers = [];
  for (const node of [...under, ...pictures].sort((a, b) =>
    a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? 1 : -1)) {
    if (!covers(node)) {
      continue;
    }
    if (node instanceof HTMLImageElement) {
      return null;
    }
    const style = getComputedStyle(node);
    const fill = node instanceof SVGElement ? style.fill : style.backgroundColor;
    if (fill === 'none') {
      continue;
    }
    const color = parseColor(fill);
    if (!color) {
      return null;
    }
    const opacity = color[3] * (node instanceof SVGElement ? Number(style.fillOpacity) * Number(style.opacity) : 1);
    if (opacity === 0) {
      continue;
    }
    layers.push([color, opacity]);
    if (opacity === 1) {
      break;
    }
  }
  let result = parseColor(getComputedStyle(stage).backgroundColor) ?? [255, 255, 255, 1];
  for (const [color, opacity] of layers.reverse()) {
    result = result.map((channel, index) => index < 3 ? color[index] * opacity + channel * (1 - opacity) : 1);
  }
  return result;
}

// A computed CSS color as [r, g, b, alpha], or null for one that isn't a plain color, such as a gradient's url.
function parseColor(value) {
  const match = /^rgba?\(([\d.]+),?\s*([\d.]+),?\s*([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\)$/.exec(value ?? '');
  if (!match) {
    return null;
  }
  const alpha = match[4] === undefined ? 1
    : match[4].endsWith('%') ? parseFloat(match[4]) / 100 : parseFloat(match[4]);
  return [Number(match[1]), Number(match[2]), Number(match[3]), alpha];
}

function hex([r, g, b]) {
  return `#${[r, g, b].map((channel) => Math.round(channel).toString(16).padStart(2, '0')).join('')}`.toUpperCase();
}

if (typeof module !== 'undefined') {
  module.exports = { inkMargins, parseColor };
}
