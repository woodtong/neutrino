internal static class WebFixture
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>GhostScreen Web Fixture</title>
<style>
  body { font: 16px sans-serif; margin: 0; }
  #btn { margin: 20px; padding: 10px 20px; }
  #name { margin: 20px; width: 300px; padding: 8px; display: block; }
  #drag { width: 200px; height: 80px; background: #90ee90; margin: 20px; }
  #wide { display: none; width: 480px; height: 10px; background: #eeeeee; }
  .spacer { height: 3000px; }
</style>
</head>
<body>
<button id="btn">Click Me</button>
<div id="status">status: idle</div>
<input id="name" placeholder="type here">
<div id="mirror">mirror: </div>
<div id="keys">keys: </div>
<div id="wheel">wheel: </div>
<div id="drag" role="button">drag</div>
<div id="dragResult">drag: idle</div>
<div id="wide"></div>
<div class="spacer"></div>
<img id="broken" src="http://127.0.0.1:9/ghostscreen-missing.png" alt=""
     style="position:absolute;width:1px;height:1px;opacity:0">
<script>
  let clicks = 0;
  document.getElementById('btn').addEventListener('click', () => {
    clicks++;
    document.getElementById('status').textContent = 'status: clicked ' + clicks;
    console.log('click', clicks);
  });
  document.getElementById('name').addEventListener('input', (event) => {
    document.getElementById('mirror').textContent = 'mirror: ' + event.target.value;
  });
  document.addEventListener('keydown', (event) => {
    if (event.ctrlKey && event.key === 's') {
      event.preventDefault();
      document.getElementById('keys').textContent = 'keys: ctrl+s';
    }
  });
  document.addEventListener('wheel', (event) => {
    document.getElementById('wheel').textContent = 'wheel: ' + Math.abs(event.deltaY);
  });
  window.addEventListener('scroll', () => {
    const probe = document.getElementById('wheel');
    if (probe.textContent === 'wheel: ') {
      probe.textContent = 'scroll: ' + Math.round(window.scrollY);
    }
  });
  const drag = document.getElementById('drag');
  let dragging = false;
  drag.addEventListener('mousedown', () => { dragging = true; });
  drag.addEventListener('mousemove', () => {
    if (dragging) document.getElementById('dragResult').textContent = 'drag: moving';
  });
  drag.addEventListener('mouseup', () => {
    dragging = false;
    document.getElementById('dragResult').textContent = 'drag: done';
  });
  console.error('boom');
  if (location.search.includes('wide=1')) {
    document.getElementById('wide').style.display = 'block';
  }
</script>
</body>
</html>
""";
}
