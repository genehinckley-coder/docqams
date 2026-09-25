// Page-wide behavior that needs no server round trip, so it is plain script rather than a component's module.

// An answer's sources start collapsed, and its [n] markers link into them. Before the link is followed, open every
// collapsed <details> around the element it points at, so it lands on something visible. The capture phase runs this
// before Blazor's own handling of the click scrolls to the target.
document.addEventListener('click', event => {
    const link = event.target instanceof Element ? event.target.closest('a[href*="#"]') : null;
    if (!link) {
        return;
    }

    const url = new URL(link.href, document.baseURI);
    if (url.origin !== location.origin || url.pathname !== location.pathname || url.search !== location.search) {
        return;
    }

    let target;
    try {
        target = document.getElementById(decodeURIComponent(url.hash.slice(1)));
    } catch {
        return; // A malformed escape names no element.
    }

    for (let details = target?.closest('details'); details; details = details.parentElement?.closest('details')) {
        details.open = true;
    }
}, true);
