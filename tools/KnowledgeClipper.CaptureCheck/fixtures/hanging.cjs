'use strict';
// Fault injection only: verifies the production host's child timeout/cleanup.
// This fixture never returns a selection and cannot prove capture success.
setInterval(() => {}, 1000);
