self.addEventListener('install', function () {
  self.skipWaiting();
});

self.addEventListener('activate', function () {
  self.clients.claim();
});

self.addEventListener('push', function (event) {
  var data = {};
  try { data = event.data ? event.data.json() : {}; } catch (e) {}
  var title = data.title || 'Weaver';
  var body = data.body || '';
  var icon = data.icon || '/weavericon.png';
  event.waitUntil(
    self.registration.showNotification(title, { body: body, icon: icon })
  );
});

self.addEventListener('message', function (event) {
  if (event.data && event.data.type === 'show-notification') {
    self.registration.showNotification(
      event.data.title || 'Weaver',
      { body: event.data.body || '', icon: event.data.icon || '/weavericon.png' }
    );
  }
});
