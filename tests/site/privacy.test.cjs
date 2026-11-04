// Run with the installed Playwright package and a Debug-built server; uses a temporary database only.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { chromium } = require(process.env.HONESTFLOW_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..');
const site = path.join(root, 'site');

async function listen(server) { server.listen(0, '127.0.0.1'); await once(server, 'listening'); return server.address().port; }
async function waitUntil(callback) { for (let i=0;i<150;i++) { if(await callback()) return; await new Promise(r=>setTimeout(r,100)); } throw new Error('Timed out'); }
const normalized = value => value.replace(/\s+/g,' ').trim();
const plainMarkdown = value => normalized(value.replace(/^#+\s+/gm,'').replace(/\*\*/g,''));

test('privacy v1.0 and request form: real local API, desktop/mobile and failure paths', { timeout: 90000 }, async t => {
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(),'honest-privacy-ui-'));
  const portProbe = http.createServer(); const apiPort = await listen(portProbe); await new Promise(r=>portProbe.close(r));
  const apiBase = `http://127.0.0.1:${apiPort}`;
  const exe = path.join(root,'bin','Debug','net10.0','HonestLicenseServer.exe');
  const api = spawn(exe,['--urls',apiBase],{cwd:tmp, windowsHide:true,env:{...process.env,
    ConnectionStrings__DefaultConnection:`Data Source=${path.join(tmp,'synthetic.db')};Pooling=False`,
    AdminApi__Key:'synthetic-site-test-key', ConnectionRequestPrivacy__Enabled:'true',
    ConnectionRequestPrivacy__ApprovedDocumentVersion:'1.0', Logging__LogLevel__Default:'Warning'}});
  let apiOutput=''; api.stdout.on('data',d=>apiOutput+=d); api.stderr.on('data',d=>apiOutput+=d);
  let browser, web;
  try {
    await waitUntil(async()=>{try{return (await fetch(apiBase+'/api/connection-requests/policy')).ok;}catch{return false;}});
    const policy = await (await fetch(apiBase+'/api/connection-requests/policy')).json();
    assert.equal(policy.available,true); assert.equal(policy.consentVersion,'1.0');
    assert.equal(policy.consentText,await fs.readFile(path.join(root,'Documents/Privacy/v1.0/consent.md'),'utf8'));
    assert.equal(policy.privacyPolicyText,await fs.readFile(path.join(root,'Documents/Privacy/v1.0/policy.md'),'utf8'));
    let mode='normal', nextPostStatus=0, posts=0;
    web=http.createServer(async(req,res)=>{
      try {
        const url=new URL(req.url,'http://localhost');
        if(url.pathname==='/api/connection-requests/policy' && mode!=='normal') {
          res.writeHead(200,{'Content-Type':'application/json'});
          return res.end(JSON.stringify(mode==='disabled'?{available:false}:{...policy,consentVersion:'2.0'}));
        }
        if(url.pathname.startsWith('/api/')) {
          const chunks=[]; for await(const chunk of req) chunks.push(chunk);
          if(req.method==='POST') {
            posts++;
            if(nextPostStatus){ const status=nextPostStatus; nextPostStatus=0; res.writeHead(status,{'Content-Type':'application/json'});return res.end('{"success":false}'); }
          }
          const response=await fetch(apiBase+url.pathname+url.search,{method:req.method,
            headers:{'Content-Type':req.headers['content-type']||'application/json'},
            body:['GET','HEAD'].includes(req.method)?undefined:Buffer.concat(chunks)});
          res.writeHead(response.status,{'Content-Type':response.headers.get('content-type')||'application/json'});
          return res.end(Buffer.from(await response.arrayBuffer()));
        }
        let file=path.resolve(site,'.'+decodeURIComponent(url.pathname));
        if(file!==site&&!file.startsWith(site+path.sep)){res.writeHead(403);return res.end();}
        if((await fs.stat(file)).isDirectory())file=path.join(file,'index.html');
        const content=await fs.readFile(file);
        res.writeHead(200,{'Content-Type':({'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css','.svg':'image/svg+xml'})[path.extname(file)]||'application/octet-stream'});
        res.end(content);
      } catch(error){res.writeHead(500);res.end('Local fixture failed');}
    });
    const base=`http://127.0.0.1:${await listen(web)}`;
    browser=await chromium.launch({channel:'chrome',headless:true});
    const context=await browser.newContext({viewport:{width:1440,height:1000}});
    await context.route('https://**',route=>route.abort());
    const page=await context.newPage(); const errors=[];page.on('pageerror',error=>errors.push(error.message));
    async function open(){ await page.goto(base); await page.locator('[data-open-request]').first().click();await page.waitForFunction(()=>!document.querySelector('[data-submit-request]').disabled); }
    async function fill(){await page.locator('[name=contactName]').fill('Synthetic tester');await page.locator('[name=phone]').fill('+7 (999) 000-00-00');await page.locator('[name=city]').fill('Test city');}
    await t.test('policy is exact, readable without JavaScript and independent of intake',async()=>{
      const nojs=await browser.newContext({javaScriptEnabled:false,viewport:{width:360,height:800}});
      const policyPage=await nojs.newPage(); await policyPage.goto(base+'/privacy/');
      const text=await policyPage.locator('main').innerText();
      const md=await fs.readFile(path.join(root,'Documents/Privacy/v1.0/policy.md'),'utf8');
      assert.equal(plainMarkdown(md),normalized((await policyPage.locator('h1').innerText())+' '+await policyPage.locator('[data-privacy-document]').innerText()));
      assert.equal(await policyPage.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
      await nojs.close();
    });
    await t.test('separate unchecked consent is required and displayed in full',async()=>{
      await open(); await fill(); const before=posts;
      assert.equal(await page.locator('[name=consent]').isChecked(),false);
      await page.locator('[data-submit-request]').click(); assert.equal(posts,before);
      await page.locator('.consent-details summary').click();
      const md=await fs.readFile(path.join(root,'Documents/Privacy/v1.0/consent.md'),'utf8');
      assert.equal(normalized(await page.locator('[data-consent-document]').innerText()),plainMarkdown(md));
      assert.equal(await page.locator('[data-consent-block] > .form-note a[href="mailto:spi@morkovka.tech"]').count(),1);
    });
    await t.test('invalid phone never posts; withdrawal link is separate from submission',async()=>{
      await page.locator('[name=phone]').fill('123');await page.locator('[name=consent]').check();const before=posts;
      await page.locator('[data-submit-request]').click();assert.equal(posts,before);
      assert.notEqual(await page.locator('[name=phone]').evaluate(el=>el.validationMessage),'');
      assert.equal(await page.locator('[data-consent-block] > .form-note a').getAttribute('href'),'mailto:spi@morkovka.tech');
      await page.locator('[name=phone]').fill('+7 (999) 000-00-00');
    });
    await t.test('mobile layout fits at 320/360/390px with expanded consent',async()=>{
      const output=process.env.HONESTFLOW_SITE_SCREENSHOTS;
      if(output) await fs.mkdir(output,{recursive:true});
      for(const width of [320,360,390]){
        await page.setViewportSize({width,height:800});
        assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
        assert.equal(await page.locator('dialog').evaluate(el=>el.scrollWidth<=el.clientWidth+1),true);
        await page.locator('[name=consent]').scrollIntoViewIfNeeded();
        if(output) await page.screenshot({path:path.join(output,`consent-${width}.png`)});
      }
      await page.setViewportSize({width:1440,height:1000});
    });
    await t.test('document conflict resets consent without silently resending',async()=>{
      await page.locator('[name=consent]').check();nextPostStatus=409;const before=posts;
      await page.locator('[data-submit-request]').click();await page.waitForFunction(()=>document.querySelector('[data-form-status]').textContent.includes('обновились'));
      assert.equal(posts,before+1); assert.equal(await page.locator('[name=consent]').isChecked(),false);
      assert.equal(await page.locator('[name=contactName]').inputValue(),'Synthetic tester');
    });
    await t.test('rate limit leaves input intact; retry succeeds through real API',async()=>{
      await page.locator('[name=consent]').check();nextPostStatus=429;
      await page.locator('[data-submit-request]').click();await page.waitForFunction(()=>document.querySelector('[data-form-status]').textContent.includes('Слишком много'));
      assert.equal(await page.locator('[name=contactName]').inputValue(),'Synthetic tester');
      await page.locator('[data-submit-request]').click();await page.waitForFunction(()=>document.querySelector('[data-form-status]').classList.contains('is-success'));
      assert.equal(await page.locator('[name=consent]').isChecked(),false);
      const list=await(await fetch(apiBase+'/api/admin/connection-requests',{headers:{'X-Admin-Key':'synthetic-site-test-key'}})).json();
      assert.equal(list.length,1);assert.equal(list[0].contactName,'Synthetic tester');
      assert.equal((await fetch(apiBase+`/api/admin/connection-requests/${list[0].id}`,{method:'DELETE',headers:{'X-Admin-Key':'synthetic-site-test-key'}})).status,204);
    });
    await t.test('disabled intake and document mismatch cannot submit',async()=>{
      for(const value of ['disabled','mismatch']){
        mode=value;await page.goto(base);await page.locator('[data-open-request]').first().click();
        await page.waitForFunction(()=>document.querySelector('[data-form-status]').textContent.length>0);
        assert.equal(await page.locator('[data-submit-request]').isDisabled(),true);
        assert.equal(await page.locator('[data-consent-document]').isVisible(),false); // collapsed, still accessible by summary
        await page.locator('.consent-details summary').click();assert.equal(await page.locator('[data-consent-document]').isVisible(),true);
      }
      mode='normal';
    });
    await t.test('privacy and application/download navigation remain available',async()=>{
      await page.goto(base+'/honestflow/');assert.equal(await page.locator('[data-release-action]').first().isVisible(),true);
      await page.goto(base+'/privacy/');assert.equal(await page.locator('[data-privacy-document] h2').count(),8);
      if(process.env.HONESTFLOW_SITE_SCREENSHOTS){
        for(const width of [360,1440]){
          await page.setViewportSize({width,height:900});
          assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
          await page.screenshot({path:path.join(process.env.HONESTFLOW_SITE_SCREENSHOTS,`privacy-${width}.png`)});
        }
      }
      assert.deepEqual(errors,[]);
    });
    await context.close();
  } finally {
    if(browser)await browser.close();if(web)await new Promise(r=>web.close(r));
    if(api.exitCode===null){api.kill();await once(api,'exit');}
    assert.ok(tmp.startsWith(path.resolve(os.tmpdir())+path.sep));await fs.rm(tmp,{recursive:true,force:true});
  }
});
