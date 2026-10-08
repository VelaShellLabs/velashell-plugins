// 截图测试用的数据:按设计稿的形状灌一个 shop 库(orders / customers / TTL carts / 时序 events / 固定 audit_log /
// 视图 / 两个 GridFS 桶 / logs 库 / 三个用户),并在单节点上初始化副本集 rs0。
// 用法:mongod --replSet rs0 起一台,然后
//   mongosh "mongodb://127.0.0.1:27017/?directConnection=true" --file tests/VelaShell.Plugin.Mongo.Tests/seed-shop.js
// ⚠️ 会 dropDatabase("shop") 并重建三个用户 —— 只对本机的测试实例跑。
try { rs.status(); } catch (e) { rs.initiate({ _id: "rs0", members: [{ _id: 0, host: "127.0.0.1:27017" }] }); }
let tries = 0;
while (!db.hello().isWritablePrimary && tries++ < 60) { sleep(500); }

const shop = db.getSiblingDB("shop");
shop.dropDatabase();
const names = ["陈立", "王芳", "李娜", "张伟", "刘洋", "赵敏", "孙浩", "周婷", "吴磊", "郑爽", "冯雪", "何军", "高远", "林夕", "罗兰", "梁晨", "宋雨"];
const levels = ["VIP", "普通", "VIP", "SVIP", "普通", "VIP", "普通", "普通", "SVIP", "普通", "VIP", "普通", "VIP", "普通", "SVIP", "普通", "VIP"];
const statuses = ["paid", "paid", "shipped", "paid", "pending", "paid", "refunded", "paid", "paid", "cancelled", "paid", "shipped", "paid", "paid", "pending", "paid", "paid"];
const skus = ["SKU-1182", "SKU-2044", "SKU-0937", "SKU-7710", "SKU-3321"];

const customers = names.map((n, i) => ({ _id: ObjectId(), name: n, level: levels[i], city: ["上海", "北京", "杭州", "深圳"][i % 4], createdAt: new Date(Date.UTC(2025, i % 12, 1 + i)) }));
shop.customers.insertMany(customers);

const orders = [];
const base = new Date("2026-09-26T09:12:00Z").getTime();
for (let i = 0; i < 1500; i++) {
  const c = customers[i % customers.length];
  const n = 1 + (i % 5);
  const items = [];
  for (let k = 0; k < n; k++) items.push({ sku: skus[(i + k) % skus.length], qty: 1 + ((i + k) % 3), price: NumberDecimal(((i * 37 + k * 13) % 900 + 99).toFixed(2)) });
  const doc = {
    orderNo: "SO2609-" + (10400 + i),
    customer: { id: c._id, name: c.name, level: c.level },
    items,
    total: NumberDecimal((((i * 7919) % 12000) + 86.5).toFixed(2)),
    status: statuses[i % statuses.length],
    paid: !["pending", "cancelled"].includes(statuses[i % statuses.length]),
    createdAt: new Date(base - i * 3600 * 1000),
    tags: i % 3 === 0 ? ["企业", "开票"] : ["个人"]
  };
  if (i % 3 === 1) doc.note = i % 2 ? "加急" : null;
  if (i % 7 === 0) doc.invoice = { title: "上海云帆科技有限公司", taxNo: "91310115MA1K4" + (100 + i) };
  orders.push(doc);
}
shop.orders.insertMany(orders);
shop.orders.createIndex({ status: 1, createdAt: -1 });
shop.orders.createIndex({ "customer.id": 1 });
shop.orders.createIndex({ orderNo: 1 }, { unique: true });
shop.orders.createIndex({ "items.sku": 1 }, { sparse: true });
shop.orders.createIndex({ note: "text" });
shop.orders.createIndex({ paidAt: 1 }, { partialFilterExpression: { paid: true } });

shop.products.insertMany(skus.map((s, i) => ({ _id: s, name: ["降噪耳机", "机械键盘", "显示器支架", "无线鼠标", "USB-C 扩展坞"][i], price: NumberDecimal((199 + i * 150).toFixed(2)), stock: 100 + i * 7, category: i % 2 ? "外设" : "音频" })));
shop.runCommand({ collMod: "products", validator: { $jsonSchema: { bsonType: "object", required: ["name", "price"], properties: { price: { bsonType: "decimal", minimum: 0 } } } }, validationLevel: "strict" });
shop.inventory.insertMany(skus.map((s, i) => ({ sku: s, warehouse: "SH-0" + (i % 3), qty: 40 + i })));
shop.reviews.insertMany(Array.from({ length: 200 }, (_, i) => ({ sku: skus[i % 5], rating: 1 + (i % 5), text: "评价 " + i, at: new Date(base - i * 7200000) })));
shop.coupons.insertMany(Array.from({ length: 40 }, (_, i) => ({ code: "C" + (1000 + i), off: 5 + (i % 4) * 5, used: i % 3 === 0 })));

shop.createCollection("carts");
shop.carts.createIndex({ updatedAt: 1 }, { expireAfterSeconds: 86400 });
shop.carts.insertMany(Array.from({ length: 30 }, (_, i) => ({ user: customers[i % 17]._id, items: [{ sku: skus[i % 5], qty: 1 }], updatedAt: new Date() })));

shop.createCollection("events", { timeseries: { timeField: "ts", metaField: "device", granularity: "minutes" }, expireAfterSeconds: 7776000 });
shop.events.insertMany(Array.from({ length: 500 }, (_, i) => ({ ts: new Date(base - i * 60000), device: { id: "d" + (i % 8), model: "T" + (i % 3) }, temp: 20 + (i % 15) })));

shop.createCollection("audit_log", { capped: true, size: 1024 * 1024 * 1024, max: 5000 });
shop.audit_log.insertMany(Array.from({ length: 100 }, (_, i) => ({ at: new Date(base - i * 1000), user: "ops_writer", action: ["update", "insert", "delete"][i % 3] })));

shop.createView("v_order_summary", "orders", [{ $group: { _id: "$status", n: { $sum: 1 }, total: { $sum: "$total" } } }]);
shop.createView("v_top_customers", "orders", [{ $group: { _id: "$customer.id", name: { $first: "$customer.name" }, sum: { $sum: "$total" } } }, { $sort: { sum: -1 } }, { $limit: 10 }]);

// GridFS buckets (small synthetic files; the driver writes chunks itself in real use).
function putFile(bucket, filename, contentType, bytes, meta) {
  const id = ObjectId();
  const chunkSize = 261120;
  const data = "x".repeat(bytes);
  let n = 0;
  for (let off = 0; off < data.length; off += chunkSize, n++) {
    shop.getCollection(bucket + ".chunks").insertOne({ files_id: id, n, data: BinData(0, Buffer.from(data.substring(off, off + chunkSize)).toString("base64")) });
  }
  shop.getCollection(bucket + ".files").insertOne({ _id: id, length: bytes, chunkSize, uploadDate: new Date(), filename, metadata: Object.assign({ contentType }, meta || {}) });
}
putFile("fs", "products/SKU-7710/main.jpg", "image/jpeg", 600000, { sku: "SKU-7710", uploader: "ops-lin" });
putFile("fs", "products/SKU-7710/main.jpg", "image/jpeg", 580000, { sku: "SKU-7710", uploader: "ops-lin" });
putFile("fs", "products/SKU-7710/detail-01.jpg", "image/jpeg", 300000, { sku: "SKU-7710" });
putFile("fs", "products/SKU-7710/spec-sheet.pdf", "application/pdf", 900000, { sku: "SKU-7710" });
putFile("fs", "products/SKU-1182/main.jpg", "image/jpeg", 200000, { sku: "SKU-1182" });
putFile("fs", "docs/manual-zh.pdf", "application/pdf", 120000, {});
shop.getCollection("fs.chunks").createIndex({ files_id: 1, n: 1 }, { unique: true });
shop.getCollection("fs.files").createIndex({ filename: 1, uploadDate: 1 });
putFile("avatars", "u/zhangwei.png", "image/png", 4000, {});
shop.getCollection("avatars.chunks").createIndex({ files_id: 1, n: 1 }, { unique: true });
shop.getCollection("avatars.files").createIndex({ filename: 1, uploadDate: 1 });

const logs = db.getSiblingDB("logs");
logs.app.insertMany(Array.from({ length: 50 }, (_, i) => ({ level: ["info", "warn", "error"][i % 3], msg: "line " + i, at: new Date() })));

const admin = db.getSiblingDB("admin");
for (const u of ["ops_reader", "ops_writer", "backup"]) { try { admin.dropUser(u); } catch (e) {} }
admin.createUser({ user: "ops_reader", pwd: "reader-pass", roles: [{ role: "read", db: "shop" }, { role: "read", db: "logs" }] });
admin.createUser({ user: "ops_writer", pwd: "writer-pass", roles: [{ role: "readWrite", db: "shop" }] });
admin.createUser({ user: "backup", pwd: "backup-pass", roles: ["backup", "restore"] });
shop.setProfilingLevel(1, { slowms: 100 });
print("seeded: " + shop.orders.countDocuments() + " orders");
