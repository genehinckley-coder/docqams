// Scrolls a paragraph of the document into view and moves keyboard focus to it, so that reading, by keyboard or screen
// reader, continues from the cited passage or the chosen section rather than from the link that led there. The
// paragraph is made focusable only here: it is not a control, so it stays out of the Tab order.
export function reveal(id, block) {
    const paragraph = document.getElementById(id);
    if (!paragraph) {
        return;
    }

    if (!paragraph.hasAttribute('tabindex')) {
        paragraph.setAttribute('tabindex', '-1');
    }

    paragraph.focus({ preventScroll: true });
    paragraph.scrollIntoView({ block: block === 'start' ? 'start' : 'center' });
}
