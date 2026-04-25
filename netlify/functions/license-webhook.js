// ELLAH-ColNum Pro — Gumroad Sale Webhook
// Receives POST from Gumroad when a purchase is made,
// generates a unique license key (identical algorithm to C# LicenseKey.cs),
// and emails it to the buyer via Resend.

const crypto = require('crypto');

// ══════════════════════════════════════════════════════
//  LICENSE KEY GENERATION
//  Mirrors C# LicenseKey.cs exactly:
//    seed  = 5 random bytes
//    hmac  = HMAC-SHA256(salt, seed)[0..4]  (first 5 bytes)
//    raw   = seed + hmac  →  10 bytes = 20 hex chars
//    key   = ELLAH-XXXXX-XXXXX-XXXXX-XXXXX
// ══════════════════════════════════════════════════════

const SALT = Buffer.from('EllahColNumPro-2026-#Str@ngS@lt!', 'utf8');

function computeHmac(seed) {
  const hmac = crypto.createHmac('sha256', SALT);
  hmac.update(seed);
  return hmac.digest().slice(0, 5); // first 5 bytes = 10 hex chars
}

function generateLicenseKey() {
  const seed = crypto.randomBytes(5);
  const mac  = computeHmac(seed);
  const raw  = Buffer.concat([seed, mac]);                   // 10 bytes
  const hex  = raw.toString('hex').toUpperCase();            // 20 hex chars
  return `ELLAH-${hex.slice(0,5)}-${hex.slice(5,10)}-${hex.slice(10,15)}-${hex.slice(15,20)}`;
}

// ══════════════════════════════════════════════════════
//  EMAIL via RESEND REST API
//  Sign up free at resend.com → create API key → add to Netlify env vars
// ══════════════════════════════════════════════════════

async function sendLicenseEmail(buyerEmail, buyerName, licenseKey, orderNumber) {
  const firstName = (buyerName || 'Engineer').split(' ')[0];

  const html = `
<!DOCTYPE html>
<html>
<head>
  <meta charset="UTF-8"/>
  <style>
    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
           background: #05060f; color: #e8eaf6; margin: 0; padding: 0; }
    .wrap { max-width: 580px; margin: 0 auto; padding: 40px 20px; }
    .logo { font-size: 1.2rem; font-weight: 700; color: #00d4ff; margin-bottom: 30px; }
    .logo span { color: #8892b0; font-weight: 300; }
    .card { background: rgba(13,16,34,0.9); border: 1px solid rgba(0,212,255,0.2);
            border-radius: 14px; padding: 32px; margin: 24px 0; }
    h1 { font-size: 1.5rem; margin: 0 0 8px; color: #e8eaf6; }
    .sub { color: #8892b0; font-size: 0.92rem; margin-bottom: 28px; }
    .key-label { font-size: 0.72rem; letter-spacing: 0.18em; text-transform: uppercase;
                 color: #00d4ff; margin-bottom: 10px; }
    .key-box { background: #05060f; border: 1px solid #00d4ff; border-radius: 8px;
               padding: 18px 24px; font-family: 'Courier New', monospace;
               font-size: 1.4rem; font-weight: 700; color: #00d4ff;
               letter-spacing: 0.12em; text-align: center; }
    .steps { margin-top: 28px; }
    .step { display: flex; gap: 14px; margin-bottom: 14px; align-items: flex-start; }
    .step-n { width: 26px; height: 26px; border-radius: 50%; background: rgba(0,212,255,0.1);
              border: 1px solid #00d4ff; display: flex; align-items: center; justify-content: center;
              font-size: 0.78rem; font-weight: 700; color: #00d4ff; flex-shrink: 0; }
    .step-t { font-size: 0.88rem; color: #8892b0; padding-top: 3px; }
    .step-t strong { color: #e8eaf6; display: block; margin-bottom: 2px; }
    .footer { text-align: center; font-size: 0.78rem; color: rgba(136,146,176,0.5); margin-top: 32px; }
    .footer a { color: #00d4ff; text-decoration: none; }
    .divider { border: none; border-top: 1px solid rgba(0,212,255,0.12); margin: 24px 0; }
  </style>
</head>
<body>
  <div class="wrap">
    <div class="logo">ELLAH<span>-ColNum Pro</span></div>

    <div class="card">
      <h1>Your License Key is Ready</h1>
      <div class="sub">Hi ${firstName}, thank you for your purchase! Copy the key below and activate your plug-in.</div>

      <div class="key-label">License Key</div>
      <div class="key-box">${licenseKey}</div>

      <hr class="divider"/>

      <div class="steps">
        <div class="step">
          <div class="step-n">1</div>
          <div class="step-t"><strong>Open Revit and click ELLAH-ColNum Pro</strong>The activation window opens automatically on first launch.</div>
        </div>
        <div class="step">
          <div class="step-n">2</div>
          <div class="step-t"><strong>Paste your license key</strong>Copy the key above and paste it into the activation field.</div>
        </div>
        <div class="step">
          <div class="step-n">3</div>
          <div class="step-t"><strong>Click Activate — you're done</strong>The plug-in is fully unlocked on this machine.</div>
        </div>
      </div>
    </div>

    <div class="footer">
      Order #${orderNumber || 'N/A'} · <a href="mailto:ellah@ellah.co.il">ellah@ellah.co.il</a><br/>
      © 2026 ELLAH Engineering Tools · This key is valid for one machine.
    </div>
  </div>
</body>
</html>`;

  const res = await fetch('https://api.resend.com/emails', {
    method:  'POST',
    headers: {
      'Content-Type':  'application/json',
      'Authorization': `Bearer ${process.env.RESEND_API_KEY}`,
    },
    body: JSON.stringify({
      from:    'ELLAH Engineering <license@ellah-colnum.com>',
      to:      buyerEmail,
      subject: `Your ELLAH-ColNum Pro License Key — Order #${orderNumber || ''}`,
      html,
    }),
  });

  if (!res.ok) {
    const txt = await res.text();
    throw new Error(`Resend API error ${res.status}: ${txt}`);
  }
}

// ══════════════════════════════════════════════════════
//  NETLIFY HANDLER
// ══════════════════════════════════════════════════════

exports.handler = async (event) => {
  // Only accept POST
  if (event.httpMethod !== 'POST') {
    return { statusCode: 405, body: 'Method Not Allowed' };
  }

  // Allow CORS preflight (not needed for webhooks but harmless)
  const corsHeaders = {
    'Access-Control-Allow-Origin': '*',
    'Content-Type': 'application/json',
  };

  try {
    // Gumroad sends application/x-www-form-urlencoded
    const params = new URLSearchParams(event.body);
    const data   = Object.fromEntries(params.entries());

    console.log('Gumroad webhook received:', {
      seller_id:    data.seller_id,
      product_id:   data.product_id,
      email:        data.email,
      order_number: data.order_number,
      test:         data.test,
      refunded:     data.refunded,
    });

    // ── Security: verify this came from OUR Gumroad account ──
    const expectedSeller = process.env.GUMROAD_SELLER_ID;
    if (expectedSeller && data.seller_id !== expectedSeller) {
      console.error('Seller ID mismatch. Got:', data.seller_id);
      return { statusCode: 401, headers: corsHeaders, body: JSON.stringify({ error: 'Unauthorized' }) };
    }

    // ── Skip refunds ──
    if (data.refunded === 'true') {
      console.log('Skipping refund event');
      return { statusCode: 200, headers: corsHeaders, body: JSON.stringify({ status: 'refund_skipped' }) };
    }

    // ── Log test sales but still process them (for testing the flow) ──
    const isTest = data.test === 'true';
    if (isTest) console.log('⚠ TEST sale — generating real key for testing');

    // ── Validate buyer email ──
    const buyerEmail = data.email;
    if (!buyerEmail || !buyerEmail.includes('@')) {
      console.error('Invalid or missing buyer email:', buyerEmail);
      return { statusCode: 400, headers: corsHeaders, body: JSON.stringify({ error: 'Invalid email' }) };
    }

    // ── Generate unique license key ──
    const licenseKey = generateLicenseKey();
    console.log(`Generated key for ${buyerEmail}: ${licenseKey}`);

    // ── Send email ──
    await sendLicenseEmail(buyerEmail, data.full_name, licenseKey, data.order_number);
    console.log(`Email sent successfully to ${buyerEmail}`);

    return {
      statusCode: 200,
      headers: corsHeaders,
      body: JSON.stringify({ success: true, message: 'License key delivered' }),
    };

  } catch (err) {
    console.error('Webhook handler error:', err.message);
    // Return 200 to prevent Gumroad from retrying on our logic errors
    return {
      statusCode: 200,
      headers: corsHeaders,
      body: JSON.stringify({ error: 'Internal error', detail: err.message }),
    };
  }
};
