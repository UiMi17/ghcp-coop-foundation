GHPC Coop Foundation — швидка пам’ятка для хоста
================================================

Перед тим як дати другу архів
------------------------------
• У Mods лежить той самий GHPC.CoopFoundation.dll (та сама збірка).
• У [GHPC_Coop_Foundation] у UserData\MelonPreferences.cfg:

  NetworkEnabled = true
  NetworkRole = "Host"
  NetworkBindPort = 27015

• Windows: дозволь вхідний UDP на порт 27015 (або той, що вибрав).
• За NAT: проброс UDP на твій ПК, другу дати публічний IP і цей порт.

Порядок: ти в Playing у місії → друг Client у тій самій місії Playing (інша місія / токен = привид не оновиться, у логу mission mismatch; на дроті зараз UDP snapshot v3).

Перевірка: у клієнта LogNetworkReceive = true — у Latest.log мають бути [CoopNet] recv.
