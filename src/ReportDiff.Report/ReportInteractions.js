document.querySelectorAll('.viewer').forEach(viewer => {
  const controls = viewer.querySelector('.image-switch');
  controls.hidden = false;
  controls.querySelectorAll('button').forEach(button => {
    button.addEventListener('click', () => {
      viewer.querySelector('.page-image').src = button.dataset.src;
      viewer.querySelector('.page-image').alt = button.dataset.label;
      viewer.querySelector('.page-image-link').href = button.dataset.src;
      viewer.querySelector('.viewer-label').textContent = button.dataset.label;
      controls.querySelectorAll('button').forEach(item => {
        item.setAttribute('aria-pressed', String(item === button));
      });
    });
  });
});

document.querySelectorAll('.exclusion-snippet').forEach(snippet => {
  const button = snippet.querySelector('.select-yaml');
  button.hidden = false;
  button.addEventListener('click', () => {
    const field = snippet.querySelector('textarea');
    field.focus();
    field.select();
  });
});
