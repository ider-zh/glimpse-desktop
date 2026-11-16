(() => {
  'use strict';
  const send = value => window.chrome.webview.postMessage(value);
  const terminal = new Terminal({
    cols:110, rows:32, fontFamily:'Cascadia Mono, Consolas, monospace', fontSize:14,
    cursorBlink:true, cursorStyle:'bar', scrollback:2000, screenReaderMode:true,
    disableStdin:true, allowProposedApi:false,
    theme:{ background:'#152333', foreground:'#e4edf5', cursor:'#7de2d0', selectionBackground:'#395d75' }
  });
  const fit = new FitAddon.FitAddon();
  terminal.loadAddon(fit);
  terminal.open(document.getElementById('terminal'));
  const fitTerminal = () => {
    const dimensions = fit.proposeDimensions();
    if (dimensions) terminal.resize(Math.max(20,Math.min(240,dimensions.cols)),Math.max(5,Math.min(100,dimensions.rows)));
  };
  new ResizeObserver(fitTerminal).observe(document.getElementById('terminal'));
  terminal.onResize(({cols,rows}) => send({type:'resize',columns:cols,rows}));
  terminal.onData(data => {
    // Includes xterm's device/status replies required by interactive terminal apps.
    for (let offset=0; offset<data.length; offset+=4096) send({type:'input',text:data.slice(offset,offset+4096)});
  });
  terminal.attachCustomKeyEventHandler(event => {
    if (event.type !== 'keydown') return true;
    if (event.ctrlKey && event.code === 'KeyC' && (event.shiftKey || terminal.hasSelection())) {
      send({type:'copy',text:terminal.getSelection()}); event.preventDefault(); return false;
    }
    if (event.ctrlKey && event.shiftKey && event.code === 'KeyV') {
      send({type:'paste'}); event.preventDefault(); return false;
    }
    return true;
  });
  window.chrome.webview.addEventListener('message', ({data}) => {
    if (!data || typeof data.type !== 'string') return;
    switch (data.type) {
      case 'output':
        if (data.reset) terminal.reset();
        if (typeof data.text === 'string') terminal.write(data.text);
        break;
      case 'enabled': {
        const activate = data.enabled && terminal.options.disableStdin;
        terminal.options.disableStdin = !data.enabled;
        if (activate) terminal.focus();
        break;
      }
      case 'paste': if(typeof data.text === 'string') terminal.paste(data.text); break;
      case 'copySelection': send({type:'copy',text:terminal.getSelection()}); break;
      case 'font': terminal.options.fontSize = Math.max(10,Math.min(22,terminal.options.fontSize + data.delta)); fitTerminal(); break;
      case 'focus': terminal.focus(); break;
      case 'fit': fitTerminal(); send({type:'resize',columns:terminal.cols,rows:terminal.rows}); break;
    }
  });
  // Small local-only inspection surface used by the opt-in synthetic WPF probe.
  // It has no host access beyond the same constrained messages as real keyboard input.
  window.glimpseTerminal = {
    text: () => Array.from({length:terminal.buffer.active.length},(_,i) => terminal.buffer.active.getLine(i).translateToString(true)).join('\n'),
    input: text => terminal.input(text,true),
    selectAll: () => terminal.selectAll(),
    selection: () => terminal.getSelection(),
    dimensions: () => ({columns:terminal.cols,rows:terminal.rows})
  };
  fitTerminal();
  send({type:'ready',columns:terminal.cols,rows:terminal.rows});
})();
