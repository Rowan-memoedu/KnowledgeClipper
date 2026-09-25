// Isolated parser fixture. No real selection, input hook or clipboard access.
console.log(JSON.stringify({ event: 'selection', data: {
  text: 'selected', posLevel: 3,
  startTop: { x: -200, y: 120 }, startBottom: { x: -200, y: 145 },
  endTop: { x: -40, y: 120 }, endBottom: { x: -40, y: 145 }
}}));
