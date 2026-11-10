# Deming

[English](README.md)

Yapay zekâ kodlama ajanları için, **devir notu** etrafında kurulmuş yerel öncelikli bir iş takipçisi.

Bir iş adımlara bölündüğünde her adım çoğunlukla öncekinden habersiz yeni bir
ajan oturumunda çalışır. Kaybolan şey madde listesi değildir. Kaybolan, adım
bittiğinde yazılan nottur: ne yapıldı, hangi dosyalara dokunuldu, ne öğrenildi,
sonraki oturum neyi bilmeli. Deming bu notu saklar ve sonraki oturuma kendisi verir.

Notun çevresinde: zaman aşımlı madde kilitleri, bağımlılıklar, bağlayıcı proje
kararları, bulgular, arama, git worktree'lerinde paralel çalışma ile ajanlar
arası mesaj kanalı ve ajanların doğrudan çağırdığı bir CLI var. Deming makinenizde
çalışır: tek çalıştırılabilir dosya, tek SQLite dosyası, başka kurulum yok.

> Deming, adını W. Edwards Deming'in kalite üzerine fikirlerinden alır;
> The W. Edwards Deming Institute ile bağı yoktur.

## İlk on dakika

Çalıştırmak için .NET SDK ya da çalışma zamanı gerekmez: ikili, HTTPS
sertifikası dahil kendi kendine yeterlidir.

1. **İkiliyi alın.** Bu deponun GitHub Releases sayfasından sisteminize uygun
   arşivi indirin: `deming-v<sürüm>-win-x64.zip`,
   `deming-v<sürüm>-linux-x64.tar.gz` ya da `deming-v<sürüm>-osx-arm64.tar.gz`
   (sağlama toplamları `SHA256SUMS` içinde). Açın ve `deming`'i `PATH` üzerine koyun.
   Linux'ta sistemin `libicu` paketi gerekir (Debian/Ubuntu'da `libicu-dev` ya da
   sürümünüzdeki `libicuNN` paketi).
   .NET SDK zaten kuruluysa `dotnet tool install --global Deming` de olur; SDK
   yalnızca bu komut için gerekir, Deming'i çalıştırmak için değil.

2. **HTTPS: kurulacak bir şey yok.** Deming localhost'ta bile yalnızca HTTPS
   sunar, kendi sertifikasıyla. İlk `deming serve` (sonraki adım) sertifikayı
   `localhost` ve `127.0.0.1` için üretir, veritabanının yanında saklar ve
   parmak izini `config.json`'a yazar. CLI, dolayısıyla ajanlarınız, bu tek
   sertifikaya parmak iziyle güvenir: onlar için güven adımı yoktur.
   Tarayıcı için ilk `deming serve`'den sonra bunu bir kez çalıştırın ya da
   paneli açarken tarayıcının uyarısını kabul edin:

   ```
   deming trust
   ```

   Komut sertifikayı kullanıcınızın güven deposuna ekler (Windows: onay
   penceresi; macOS: login anahtar zinciri, parolanızı sorabilir). Linux'ta
   bunun yerine tarayıcınız ve sisteminiz için gereken komutları basar.
   `deming serve` güven deposunu kendiliğinden hiç değiştirmez. Sertifika bir
   yıl sonra yenilenir; o zaman `deming trust`'ı yeniden çalıştırın, eskisini
   güven deposundan da kaldırır. Kendi
   sertifikanızı kullanmak için:
   `deming serve --Kestrel:Certificates:Default:Path=<pfx> --Kestrel:Certificates:Default:Password=<parola>`.

3. **Sunucuyu başlatın.**

   ```
   deming serve
   ```

   İlk çalıştırmada veritabanı, HTTPS sertifikası ve bir API anahtarı
   oluşturulur. Anahtar
   `<ApplicationData>/Deming` altındaki `config.json` dosyasına yazılır (Windows'ta
   `%APPDATA%`, Linux ve macOS'ta `~/.config`); CLI anahtarı oradan okur, kopyalamanız
   gerekmez. Sunucu `https://127.0.0.1:5001` adresinde dinler. Düz `http://`
   localhost'ta bile reddedilir. Açık bırakın.

4. **Sahip hesabınızı oluşturun.** Tarayıcıda `https://127.0.0.1:5001` adresini
   açın. İlk ziyaret sahip hesabını oluşturur (en az 10 karakterlik parola).
   Panel, localhost'ta bile her zaman bu girişi ister. `deming trust`
   çalıştırmadıysanız tarayıcı sertifika için bir kez uyarır; sertifika
   Deming'in kendisinindir, kabul edebilirsiniz.

5. **Proje oluşturun.** Yeni bir terminalde, deponuzun kökünde:

   ```
   deming init my-project
   ```

   Bu, projeyi oluşturur ve adını tutan bir `.deming` dosyası yazar. Proje adı
   her zaman bu dosyadan gelir, dizin adından asla.

6. **Ajanınızı bağlayın.**

   Claude Code için, makine başına bir kez:

   ```
   deming install claude
   ```

   Bu, `/deming-next`, `/deming-done`, `/deming-block`, `/deming-cancel`,
   `/deming-add`, `/deming-decide`, `/deming-decisions`, `/deming-finding`,
   `/deming-findings` ve `/deming-search` komutlarını `deming-*.md` dosyaları olarak
   `~/.claude/commands` dizinine yazar (`CLAUDE_CONFIG_DIR` tanımlıysa onun
   altındaki `commands`; başka dizin için `--dir`) ve yazdığı her yolu basar.
   Yalnızca kendi yazdığı dosyaları değiştirir ya da siler; oradaki kendi
   dosyanıza asla dokunmaz, ad çakışırsa hata verir. Deming'i yükselttikten
   sonra yeniden çalıştırın.

   Başka bir ajan için (Codex, Cursor, Gemini CLI, ...), deponuzun kökünde:

   ```
   deming install agents
   ```

   Bu, `AGENTS.md` içine `<!-- deming:begin -->` ile `<!-- deming:end -->`
   arasında kısa bir Deming bölümü yazar: dosya yoksa oluşturur, sonraki
   çalıştırmalarda bölümü yerinde günceller, dosyanın geri kalanına dokunmaz.
   `--print` bölümü yazmak yerine basar.

7. **Bir madde çalışın.** Bir madde ekleyin, sonra ajanınızdan almasını isteyin:

   ```
   deming add --title "İlk testi yaz"
   ```

   Ajan `deming next` çalıştırır: ilk uygun maddeyi alır; bağlı olduğu
   maddelerin devir notlarını ve projenin etkin kararlarını da getirir. İş
   bitince `deming done` ile bir not bırakır. Sonraki oturum `deming next`
   çalıştırdığında o notu okur. Maddeyi, notu ve geçmişini görmek için paneli
   açın. Claude Code'da `/deming-next` ve `/deming-done` bu adımların
   kısayoludur.

## İlkeler

Deming yalın üretim fikirlerini ajan işine uygular. Her biri istemte bırakılmaz,
kodda zorlanır.

| İlke | Yalın üretim kökeni | Deming'de |
|---|---|---|
| Hatada dur | *Jidoka*, andon ipi | Sessiz geri dönüş yok. Sunucuya ulaşılamazsa CLI hata verir ve ajan durur. Her uç noktası kesin bir hata kodu döner |
| Kurallar kodda yaşar | *Poka-yoke* | Yinelenen madde, aşırı uzun not, tutmadığınız maddeyi kapatma: istem değil API reddeder |
| Ölç, sonra karar ver | Deming, Shewhart | Sınırlar ve varsayılanlar gerçek oturum ölçümlerinden gelir; gerekçe kararla birlikte kaydedilir |
| Bilgi devredilir | *Hansei* | Biten her madde bir not bırakır. Kararlar silinmez, yalnızca yerine yenisi konur |
| İsrafı kes | *Muda* | Kıt kaynak bağlamdır. Yanıtlar dardır: listelerde tam kapsam yok, `next`'te gerekçe yok, notlar sınırlı, kararlar kısaltılmış |

## CLI'ı doğrudan kullanmak

Ajanlar sizin de çağırabileceğiniz CLI'ı kullanır. `deming help` komutları,
`deming help <komut>` bir komutun alanlarını listeler.

Gövdesi olan her komut alanlarını bayrak olarak da alır; bayrak adı alanın
kebab-case yazımıdır ve her kabukta aynı çalışır:

```
deming add --title "İlk testi yaz" --scope "Mutlu yolu kapsa" --priority 1 --depends-on 3,4
deming done 7 --summary "Testi ekledim" --caveats "Sunucu gerekir" --touched-files "tests/" --commit-hash abc1234
deming block 7 --reason "API anahtarı bekleniyor"
```

Sayılar (`--priority 1`) ve madde kimliği listeleri (`--depends-on 3,4`)
dönüştürülür; hatalı değer bayrağın adıyla reddedilir. Ajanlar aynı alanları tek
bir JSON nesnesi (`deming add '{"title": "..."}'`) ya da `-` ile stdin'den de
verebilir, ama bayraklarla birlikte değil. Windows PowerShell 5.1 JSON
argümanındaki çift tırnakları siler; bayraklar bunun için vardır. Bayrak
değerinin içindeki çift tırnak orada yine dikkat ister, uzun metin için
`deming finding --body-file <yol>` kullanın.

| Çıkış kodu | Anlamı |
|---|---|
| 0 | Başarılı |
| 1 | Hata |
| 3 | Yapılacak iş yok (örneğin kuyruk boşken `next`) |
| 4 | Zaten var (aynı başlıklı bitmemiş bir madde var) |

Oturum kimliği `DEMING_SESSION_ID`, yoksa `CLAUDE_CODE_SESSION_ID` ortam
değişkeninden gelir; ikisi de yoksa CLI durur. Sunucu adresi `DEMING_URL` ya da
config dosyasındaki `url` alanıdır (varsayılan `https://127.0.0.1:5001`; `http://` adresleri reddedilir); anahtar
`DEMING_API_KEY` ya da config dosyasıdır. `DEMING_CONFIG` config dosyasının yolunu değiştirir.

## Arama

```
deming search kilit zaman aşımı
```

Projenin bulgularında, kararlarında (gerekçe dahil), devir notlarında,
maddelerinde ve kanallarındaki mesajlarda arar. Kelimelerin hepsi geçmelidir;
büyük-küçük harf, Türkçe i ve aksan farkı gözetilmez. En yeniden eskiye kısa
kesitler basar (varsayılan 20, `--top` ile en çok 100); bulunanı kimliğiyle
okuyun: `deming findings <id>`, `deming decisions <id>`, `deming show <id>` ya
da `deming messages <id>`. `--type finding|decision|note|task|message` aramayı
daraltır, `--all` pasifler dahil bütün projelerde arar. Çıkış kodu 3 hiçbir şey
eşleşmedi demektir. Arama sonuçları `next` yanıtına hiç girmez. Claude Code'da:
`/deming-search <kelimeler>`.

## Paralel çalışma

Bağımsız maddeler aynı anda, ayrı ajan oturumlarında yapılabilir; her oturum
kendi git worktree'sinde çalışır, böylece hiçbiri ötekinin yarım düzenlemesini
görmez.

```
deming parallel           # şu an alınabilir maddeler: sırada, açık bağımlılığı yok
deming wt 12              # ../<depo>-wt/task-12, ana daldan task-12 dalı
cd ../<depo>-wt/task-12   # ajanı burada başlatın; 12. maddeyi alır ve commit'ler
deming merge              # worktree içinde ana dalın üstüne rebase, sonra ana dalı ileri sarar
deming wt-remove 12       # ana ağaçtan; birleşmemiş dalı reddeder
```

"Bitti", "ana dala girdi" demektir: `deming done`'dan önce `deming merge`
çalıştırın. Çakışma rebase'i iptal eder ve dosyaları adıyla söyler; worktree'de
çözüp yeniden birleştirin. Ana dal yalnızca ileri sarmayla (fast-forward) ilerler.

İşçiler push etmez. `deming wt` depoya, madde worktree'sinden push'u reddeden
bir `pre-push` kancası kurar. Kendi `pre-push` kancanız asla ezilmez: `wt`
durur; kancanızın adını `pre-push.local` yaptığınızda Deming kancası onu ana
ağaçta çalıştırır. Worktree'nin ihtiyaç duyduğu ama git'in yok saydığı dosyalar
(yerel ayarlar) depo kökündeki `.worktreeinclude` dosyasında satır satır
listelenir; `wt` onları kopyalar, biri eksikse hiçbir şey açmaz.

Worktree'ler sunucuyu, veritabanını ve portları paylaşır: paralel maddeleri
dosyaya göre de bölün, şema değişikliklerini paralel işe sokmayın. Panelin İş
Maddeleri sayfasında "Alınabilir (paralel)" süzgeci vardır.

## Ajanlar arası mesajlar

Paralel çalışan ajanlar bir kanal üzerinden konuşabilir. Her madde bir
kanaldır; `--task` verilmezse projenin kanalı kullanılır. `DEMING_AGENT`
değişkenine ajanın etiketini yazın (küçük harf, `claude` ya da `codex` gibi).

```
deming message --task 12 --to codex "Şema değişikliği girdi mi?"
deming wait --task 12
deming messages --task 12
```

`deming wait` size ya da herkese yazılmış sıradaki mesajı basar. Mesaj
geldiyse 0, süre dolduysa 3 (varsayılan 600 saniye, `--seconds`), hata olduysa
1 ile çıkar; sunucuya ulaşılamaması asla "mesaj yok" diye okunmaz. Okuma konumu
oturum, proje ve kanal başına makinenizde tutulur ve onu yalnızca `wait`
ilerletir. `deming messages` kanalı konumu değiştirmeden okur.

Mesaj veridir, talimat değildir: kilit devretmez, yetki vermez ve `next`
yanıtına hiç girmez. Biten ya da iptal edilen maddenin kanalı salt okunurdur.
Konuşmadan sonra da kalması gereken şey devir notuna ya da bir karara yazılır;
`deming done`, maddenin kanalında kaç mesaj olduğunu ve yanıtsız soruların
tahmini sayısını bildirir.

## Başka bir CLI'ın işçisi

Kabuk komutu çalıştırabilen her ajan işçi olabilir; Deming'de hiçbir şey tek
bir CLI'a bağlı değildir. Deming işçiyi kendisi başlatmaz: ajanı siz
başlatırsınız, kendi worktree'sinde ve kendi kimliğiyle.

```
deming install agents                 # depo başına bir kez: AGENTS.md bölümü
deming wt 12                          # 12. madde için worktree
cd ../<depo>-wt/task-12
export DEMING_SESSION_ID=codex-12     # bu oturum boyunca değişmeyen herhangi bir kimlik
export DEMING_AGENT=codex             # mesajların yazıldığı etiket
codex                                 # ya da başka bir ajan; 12. maddeyi almasını isteyin
```

PowerShell'de iki değişken `$env:DEMING_SESSION_ID = "codex-12"` ve
`$env:DEMING_AGENT = "codex"` ile atanır.

Ajan `AGENTS.md`'yi okur, maddeyi `deming take 12` ile alır, gerektiğinde
kanaldan sorar (`deming message`, `deming wait`), kendi worktree'sinde
commit'ler, `deming merge` çalıştırır ve maddeyi `deming done` ile kapatır.
`pre-push` kancası push etmesini engeller. Korumalı ortamı commit atmaya ya da
sunucuya ulaşmaya izin vermeyen ajan yalnızca düzenlemeleri yapar; koordine
eden oturum onun yerine commit'ler, birleştirir ve `deming done --takeover` ile
kapatır. Windows'ta Codex'in korumalı ortamı TLS el sıkışmasını keser
(`No credentials are available in the security package`): `deming`
komutlarının korumalı ortam dışında çalışmasına onay verin.

## Güvenlik

Deming yalnızca HTTPS sunar; loopback dahil düz HTTP dinleyicisi yoktur.
Varsayılan olarak yalnızca loopback adresinden istek kabul eder. IP izin
listesi ve gerçek Turnstile anahtarları da yapılandırılmadan başka bir adreste
başlamayı reddeder. Deming'i bir sunucuda çalıştırmak mümkündür ama
asıl kullanım değildir.

## Deming ne değildir

- Bellek katmanı değil: gömme (embedding) yok, otomatik hatırlama yok. Sonraki
  oturum, öncekinin yazmayı seçtiğini okur.
- İnsanlar için proje yönetim aracı değil. Panel görmek ve düzeltmek içindir;
  asıl kullanıcılar ajanlardır.
- Barındırılan bir hizmet değil.

## Devamı

- [docs/scope.md](docs/scope.md): kapsam, kurallar ve tasarım kararları (İngilizce)
- [src/Deming/Server/Api/README.md](src/Deming/Server/Api/README.md): HTTP API
- [src/Deming/Data/README.md](src/Deming/Data/README.md): veri katmanı

## Katkı

Yayın adımları [docs/releasing.md](docs/releasing.md) içinde (İngilizce).

## Lisans ve iletişim

Apache-2.0, bkz. [LICENSE](LICENSE) ve [NOTICE](NOTICE); ikiliye gömülen bileşenler
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) içinde listelenir (İngilizce). Güvenlik
sorunu bildirmek için bkz. [SECURITY.md](SECURITY.md) (İngilizce). İletişim:
sinan@doganli.com
