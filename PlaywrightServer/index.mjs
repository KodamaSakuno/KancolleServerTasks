import { chromium } from "playwright";
import { Mutex } from "async-mutex";
import { createServer, createConnection } from "node:net";

let browser = null;
const mutex = new Mutex();

async function ensureBrowser() {
    return await mutex.runExclusive(async () => {
        return browser ??= await chromium.launchServer({ wsPath: "/" });
    });
}
async function closeBrowser() {
    await browser.close();
    browser = null;

    console.log("Browser closed");
}

const activeConnections = new Set();

const server = createServer(async client => {
    console.log(`New connection from ${client.remoteAddress}:${client.remotePort}`);

    const browser = await ensureBrowser();
    const wsEndpoint = new URL(browser.wsEndpoint());
    const server = createConnection(wsEndpoint.port);

    server.pipe(client);
    client.pipe(server);

    activeConnections.add(server);

    server.once("close", async () => {
        activeConnections.delete(server);

        if (activeConnections.size === 0) {
            await closeBrowser();
        }
    });
});

server.listen(10000, () => {
    const { address, port } = server.address();

    console.log(`Server listening on ${address}:${port}`);
});
