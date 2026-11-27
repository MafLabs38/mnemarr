// The transcription service of the end-to-end stack (ADR 0047): it speaks OpenAI's /audio/transcriptions, so Mnemarr's real OpenAiCompatible provider calls it, and answers a
// fixed sentence for any recording. It refuses a form without a recording or a model, as the real service does, so that what Mnemarr sends is checked too.

import { createServer } from 'node:http';

const sentence = 'Put this idea in the incubator.';

createServer((request, response) => {
  if (request.method !== 'POST' || request.url !== '/v1/audio/transcriptions') {
    response.writeHead(404).end();
    return;
  }

  const chunks: Buffer[] = [];
  request.on('data', (chunk: Buffer) => chunks.push(chunk));
  request.on('end', () => {
    const form = Buffer.concat(chunks).toString('latin1');
    const complete = form.includes('name="file"') && /name="model"\r\n\r\n[\w.:/-]+/.test(form);
    response.writeHead(complete ? 200 : 400, { 'content-type': 'application/json' });
    response.end(
      JSON.stringify(
        complete
          ? { text: sentence, usage: { type: 'duration', seconds: 1 } }
          : { error: { message: 'no file or model' } },
      ),
    );
  });
}).listen(8492);
