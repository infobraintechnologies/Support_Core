"use strict";

document.addEventListener("DOMContentLoaded", function () {
    const adminRadio = document.getElementById('adminRole');
    const clientRadio = document.getElementById('clientRole');
    const roleSelectorSlider = document.querySelector('.role-selector-slider');

    const adminForm = document.getElementById('adminForm');
    const clientForm = document.getElementById('clientForm');

    const adminInputs = adminForm.querySelectorAll('input');
    const clientInputs = clientForm.querySelectorAll('input');

    function handleRoleChange() {
        if (clientRadio.checked) {
            roleSelectorSlider.classList.add('client');

            adminForm.hidden = true;
            clientForm.hidden = false;

            adminInputs.forEach(input => input.disabled = true);
            clientInputs.forEach(input => input.disabled = false);

        } else {
            roleSelectorSlider.classList.remove('client');

            adminForm.hidden = false;
            clientForm.hidden = true;

            adminInputs.forEach(input => input.disabled = false);
            clientInputs.forEach(input => input.disabled = true);
        }
    }

    if (adminRadio) {
        adminRadio.addEventListener('change', handleRoleChange);
    }
    if (clientRadio) {
        clientRadio.addEventListener('change', handleRoleChange);
    }

    handleRoleChange();
});
