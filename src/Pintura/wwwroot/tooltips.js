// A themed tooltip for anything with a title, shown after a pause and following the cursor.
const TOOLTIP_DELAY = 200;
let tooltipLayer = null;
let tooltipHost = null;
let tooltipTimer = 0;
let tooltipPointer = null;

function tooltipFor(target) {
  // A frame's title names it for screen readers, and the pointer inside it never reaches this page to hide the tooltip.
  return target.closest?.('[data-tooltip], [title]:not(iframe), [data-tooltip-title]') ?? null;
}

function tooltipText(host) {
  return host.dataset.tooltip ?? host.dataset.tooltipTitle ?? host.getAttribute('title') ?? '';
}

function showTooltip(host) {
  if (host === tooltipHost) {
    return;
  }
  hideTooltip();
  const text = tooltipText(host);
  if (!text.trim()) {
    return;
  }
  const native = host.getAttribute('title');
  if (native !== null) {
    // Held aside while ours shows and restored after, so it stays in the accessible name.
    host.dataset.tooltipTitle = native;
    host.removeAttribute('title');
  }
  if (tooltipLayer === null) {
    tooltipLayer = document.createElement('div');
    tooltipLayer.className = 'tooltip';
    tooltipLayer.setAttribute('role', 'presentation');
    document.body.appendChild(tooltipLayer);
  }
  tooltipHost = host;
  tooltipLayer.textContent = text;
  tooltipLayer.hidden = false;
  placeTooltip(host);
}

function pointerInside(host) {
  if (tooltipPointer === null) {
    return false;
  }
  const bounds = host.getBoundingClientRect();
  return tooltipPointer.x >= bounds.left && tooltipPointer.x <= bounds.right
    && tooltipPointer.y >= bounds.top && tooltipPointer.y <= bounds.bottom;
}

function placeTooltip(host) {
  const margin = 8;
  const box = tooltipLayer.getBoundingClientRect();
  // Follow the cursor, or the element under keyboard focus.
  const anchor = pointerInside(host)
    ? { left: tooltipPointer.x, right: tooltipPointer.x, top: tooltipPointer.y - 6, bottom: tooltipPointer.y + 14 }
    : host.getBoundingClientRect();
  let top = anchor.top - box.height - margin;
  let placement = 'top';
  if (top < margin) {
    top = anchor.bottom + margin;
    placement = 'bottom';
  }
  const left = Math.min(Math.max((anchor.left + anchor.right) / 2 - box.width / 2, margin),
    Math.max(margin, window.innerWidth - box.width - margin));
  tooltipLayer.dataset.placement = placement;
  tooltipLayer.style.top = `${top}px`;
  tooltipLayer.style.left = `${left}px`;
}

function hideTooltip() {
  clearTimeout(tooltipTimer);
  if (tooltipHost !== null) {
    const held = tooltipHost.dataset.tooltipTitle;
    if (held !== undefined) {
      // A title set while ours showed is newer than the one held aside.
      if (!tooltipHost.hasAttribute('title')) {
        tooltipHost.setAttribute('title', held);
      }
      delete tooltipHost.dataset.tooltipTitle;
    }
    tooltipHost = null;
  }
  if (tooltipLayer !== null) {
    tooltipLayer.hidden = true;
  }
}

function setupTooltips() {
  document.addEventListener('pointermove', (event) => {
    tooltipPointer = { x: event.clientX, y: event.clientY };
    if (tooltipHost !== null) {
      placeTooltip(tooltipHost);
    }
  }, { passive: true });
  document.addEventListener('pointerover', (event) => {
    const host = tooltipFor(event.target);
    if (host === null) {
      hideTooltip();
      return;
    }
    if (host === tooltipHost) {
      return;
    }
    hideTooltip();
    tooltipTimer = setTimeout(() => showTooltip(host), TOOLTIP_DELAY);
  });
  // Pointer-only, like the native title: focus dismisses a tooltip but never raises one.
  document.addEventListener('focusin', hideTooltip);
  document.addEventListener('pointerdown', hideTooltip);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      hideTooltip();
    }
  }, true);
  document.addEventListener('scroll', hideTooltip, true);
  window.addEventListener('blur', hideTooltip);
}

setupTooltips();
