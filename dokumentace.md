# Popis
- Na začátku sestavím index počátku každého řádku.
- Poté udržuji v paměti (proměnná Lines) aktuální (tedy zobrazené) řádky +- buffer.
- Pomocí tohoto indexu pak čtu ze souboru na disku řádky dle potřeby.
- Předpokládá se UTF-8 kódování.

# Možnosti vylepšení
- Index budovat asynchronně, tj. okamžitě otevřít soubor, kde uživatel může začít pracovat s částí občasu, a mezitím dokončit indexaci.
- Budování indexu má nejspíše prostor na optimalizaci. 
  - Pouze načíst soubor (50 mil. řádků) trvá 8 sekund, budovat současně index pak 18 sekund.
- Index nemusí obsahovat každý řádek, ale může být řídký a obsahovat třeba každý tisícatý. 
- Vylepšit/změnit animaci Home/End u velkých souborů.

