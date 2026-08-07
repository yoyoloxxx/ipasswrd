// Проверка правил разбора полей формы доставки — без браузера и без сейфа.
//
// Правила живут в extension/content.js регулярками; ошибка в них не падает, а тихо
// раскладывает данные не по тем полям (почта в поле индекса — уже случалось).
// Здесь те же выражения прогоняются по подписям, какие реально встречаются в формах.
//
//   node tools\store\check-field-rules.js

const zip    = (t) => /индекс|postal ?code|post ?code|zip|postindex/.test(t);
const email  = (t) => /e[-\s]?mail|электронн\w* почт|почт[аеуы]([^а-яё]|$)/.test(t);
const phone  = (t) => /телефон|phone|mobile/.test(t);
const city   = (t) => /(^|[^а-яё])город|\bcity\b|town|locality/.test(t);
const country= (t) => /страна|country/.test(t);
const street = (t) => (/улиц|(^|[^а-яё])адрес|street|address|\baddr\b|addr[1-2]?\b/.test(t)) &&
                      !/электронн|e[-\s]?mail|\bmail\b/.test(t);
const last   = (t) => /(^|[^а-яё])фамили|last ?name|lastname|surname|\blname\b/.test(t);
const middle = (t) => /отчеств|middle ?name|middlename|patronymic|\bmname\b/.test(t);
const full   = (t) => /\bфио\b|получател|recipient|full ?name/.test(t);
const first  = (t) => /(^|[^а-яё])имя([^а-яё]|$)|first ?name|firstname|\bfname\b/.test(t);

// Порядок ровно тот же, что в fillIdentity: он и есть часть правил.
function decide(t) {
  if (last(t)) return "lastName";
  if (middle(t)) return "middleName";
  if (full(t)) return "fio";
  if (first(t)) return "firstName";
  if (phone(t)) return "phone";
  if (zip(t)) return "zip";
  if (email(t)) return "email";
  if (country(t)) return "country";
  if (city(t)) return "city";
  if (street(t)) return "street";
  return "-";
}

const cases = [
  ["surname фамилия", "lastName"],
  ["lname last name", "lastName"],
  ["patronymic отчество", "middleName"],
  ["recipient имя получателя", "fio"],
  ["fio фио получателя", "fio"],
  ["firstname имя", "firstName"],
  ["f first name", "firstName"],
  ["phone контактный телефон", "phone"],
  ["zipcode почтовый индекс", "zip"],
  ["postindex индекс", "zip"],
  ["z zip", "zip"],
  ["mail электронная почта", "email"],
  ["email адрес электронной почты", "email"],
  ["e e-mail", "email"],
  ["country страна", "country"],
  ["town город", "city"],
  ["ct city", "city"],
  ["addr улица, дом, квартира", "street"],
  ["delivery_address адрес доставки", "street"],
  ["sa street address", "street"],
  ["postal address почтовый адрес", "street"],
];

let bad = 0;
for (const [text, want] of cases) {
  const got = decide(text);
  if (got !== want) { bad++; console.log(`FAIL  "${text}" -> ${got}, ждали ${want}`); }
}
console.log(bad === 0 ? `OK: ${cases.length} подписей разложены верно` : `ОШИБОК: ${bad}`);
process.exit(bad === 0 ? 0 : 1);
