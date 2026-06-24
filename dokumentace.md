# Popis
- Na začátku sestavím index počátku každého řádku

# TODO
- scrollovani moc nefunguje.. kdyz scrolluju rucne, po chvili to prestane. Kdyz pak scrollnu druhym smerem, jakoby po pokracuje v tom, v cem melo puvodne
- virtual scroll bar funguje asi celkem dobre ale je zasekanej
- 

# Rozhodnutí

- Předpokládá se UTF-8 kódování
- Řádky se newrapují
- Každý řádek ze souboru je jeden TextBlock komponent. Lépe se pak ve vyhledávání zvýrazňují
- Soubor se otevře okamžitě (stream) a ihned se začne v pozadí indexovat pro rychlý random access.