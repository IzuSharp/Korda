CREATE TABLE rcaa_items ("Id" bigint PRIMARY KEY);
INSERT INTO rcaa_items SELECT generate_series(1,4096)::bigint;
ANALYZE rcaa_items;
SELECT count(*),sum("Id"),min("Id"),max("Id") FROM rcaa_items;
