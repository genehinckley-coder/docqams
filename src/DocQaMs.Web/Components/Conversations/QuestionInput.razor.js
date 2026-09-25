// Enter sends the question and Shift+Enter inserts a newline. The newline is prevented only for Enter without Shift,
// and never while an IME is composing, where Enter confirms the composition instead. keyCode 229 covers browsers that
// report a composition's keydown without isComposing.
export function attach(textarea, dotNet) {
    const onKeyDown = (event) => {
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229) {
            return;
        }

        event.preventDefault();
        dotNet.invokeMethodAsync('SubmitFromKeyboard', textarea.value);
    };

    textarea.addEventListener('keydown', onKeyDown);
    textarea.docqaOnKeyDown = onKeyDown;
}

export function detach(textarea) {
    if (textarea && textarea.docqaOnKeyDown) {
        textarea.removeEventListener('keydown', textarea.docqaOnKeyDown);
        delete textarea.docqaOnKeyDown;
    }
}
