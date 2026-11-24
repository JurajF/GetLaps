// Show/hide and copy buttons for LAPS secrets. Kept in a file (no inline script) so the
// Content-Security-Policy can forbid inline JavaScript.
(function () {
    function valueOf(el) {
        return el.dataset.secret !== undefined ? el.dataset.secret : el.textContent;
    }

    // Forms with data-confirm ask before submitting (used for "Request password rotation").
    document.addEventListener("submit", function (e) {
        const form = e.target.closest("form[data-confirm]");
        if (form && !window.confirm(form.dataset.confirm)) {
            e.preventDefault();
        }
    });

    document.addEventListener("click", async function (e) {
        const reveal = e.target.closest("[data-reveal]");
        if (reveal) {
            const el = document.getElementById(reveal.dataset.reveal);
            const masked = el.classList.toggle("masked");
            el.textContent = masked ? "••••••••••••" : el.dataset.secret;
            reveal.textContent = masked ? "Show" : "Hide";
            return;
        }

        const copy = e.target.closest("[data-copy]");
        if (copy) {
            const el = document.getElementById(copy.dataset.copy);
            try {
                await navigator.clipboard.writeText(valueOf(el));
                const label = copy.textContent;
                copy.textContent = "Copied";
                setTimeout(function () { copy.textContent = label; }, 1500);
            } catch {
                copy.textContent = "Copy failed";
            }
        }
    });
})();
