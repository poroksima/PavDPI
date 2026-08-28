[Poroksima — guns.lol](https://guns.lol/poroksima)

# PavDPI 2.0

Poroksima tarafından geliştirilmiştir. / Developed by Poroksima.
GitHub: https://github.com/poroksima


## TÜRKÇE

### PavDPI Nedir?

PavDPI, Windows 10 ve Windows 11 için geliştirilmiş tek dosyalık bir DPI
aracıdır. Discord ve Roblox erişimini düzeltmeyi, bunu yaparken genel web
trafiğini mümkün olduğunca etkilememeyi hedefler.

### Hızlı Kurulum

1. PavDPI.exe dosyasını aç.
2. Windows yönetici/UAC onayını kabul et.
3. PavDPI gerekli bağlantı ayarlarını otomatik olarak kontrol eder.
4. "PavDPI kuruldu ve doğrulandı" uyarısında Tamam'a bas.

Kurulum tamamlanınca uygulama kendiliğinden kapanır. Bilgisayarı yeniden
başlattığında PavDPI Service otomatik olarak çalışır; PavDPI.exe dosyasını
her açılışta yeniden çalıştırman gerekmez.

### Kullanım

PavDPI.exe dosyasını daha sonra yeniden açarak şu işlemleri yapabilirsin:

- Kur veya onar: Kurulumu yeniler ve çalışan ayarı otomatik seçer.
- Bağlantıyı test et: Discord ve Roblox bağlantılarını kontrol eder.
- Kaldır: PavDPI hizmetini ve kurulu program dosyalarını kaldırır.

### Özellikler

- Tek PavDPI.exe ile kurulum, onarım, test ve kaldırma
- Windows ile birlikte otomatik başlatma
- Discord ve Roblox için otomatik bağlantı kontrolü
- Gerekli ayarı kullanıcıdan teknik seçim istemeden belirleme
- Yalnızca hedef alan adlarına uygulanan DPI kuralları
- 195.175.254.2 hatalı DNS yanıtını algılama
- Motor durursa kontrollü yeniden başlatma
- Başarısız kurulumda önceki çalışan duruma geri dönme
- Yerel ve boyutu sınırlı tanı günlükleri
- Reklam, telemetri, toast bildirimi veya sistem tepsisi simgesi yok

### Kaldırma

PavDPI.exe dosyasını aç ve "Kaldır" düğmesine bas. PavDPI Service ile
C:\Program Files\PavDPI klasörü kaldırılır. Sorun incelemesi için yerel tanı
günlükleri korunur.

### Günlükler

Kurucu: C:\ProgramData\PavDPI\installer.log
Motor:  C:\ProgramData\PavDPI\logs\PavDPI.log
Durum:  C:\ProgramData\PavDPI\state.txt

Bu günlükler yalnızca bilgisayarında tutulur ve herhangi bir sunucuya
gönderilmez.

### Güvenlik ve Gizlilik

PavDPI bir VPN değildir. IP adresini gizlemez ve internet trafiğine ek
şifreleme sağlamaz. Windows hizmeti ve ağ bileşenleri için yönetici izni
ister; UAC onayını atlatmaz. Reklam göstermez, telemetri toplamaz ve
internetten sessizce çalıştırılabilir dosya indirmez.

### Kaynaktan Derleme

Gereksinimler:

- 64-bit Windows 10 veya Windows 11
- .NET Framework 4.x C# derleyicisi
- PowerShell 5.1 veya daha yeni

Derleme:

powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1

Tüm testler:

powershell -ExecutionPolicy Bypass -File .\tests\run-all.ps1

Derlenen uygulama release\PavDPI.exe konumunda oluşturulur.

Proje: https://github.com/poroksima


## ENGLISH

### What is PavDPI?

PavDPI is a single-file DPI utility for Windows 10 and Windows 11. It is
designed to restore access to Discord and Roblox while minimizing its impact
on general web traffic.

### Quick Installation

1. Open PavDPI.exe.
2. Approve the Windows administrator/UAC prompt.
3. PavDPI automatically checks the required connection settings.
4. Press OK when the "PavDPI is installed and verified" message appears.

The application closes automatically after installation. PavDPI Service
starts automatically the next time Windows starts, so you do not need to
open PavDPI.exe after every reboot.

### Usage

Open PavDPI.exe again whenever you want to use these actions:

- Install or repair: Refreshes the installation and selects a working setting.
- Test connection: Checks Discord and Roblox connectivity.
- Uninstall: Removes the PavDPI service and installed program files.

### Features

- One PavDPI.exe for installation, repair, testing, and removal
- Automatic startup with Windows
- Automatic Discord and Roblox connection checks
- Selects the required setting without asking for technical choices
- DPI rules are limited to target domains
- Detects the invalid DNS response 195.175.254.2
- Controlled engine restart if it stops unexpectedly
- Restores the previous working state if installation fails
- Local, size-limited diagnostic logs
- No ads, telemetry, toast notifications, or system tray icon

### Uninstall

Open PavDPI.exe and press "Uninstall". PavDPI Service and the
C:\Program Files\PavDPI directory are removed. Local diagnostic logs are
retained for troubleshooting.

### Logs

Installer: C:\ProgramData\PavDPI\installer.log
Engine:    C:\ProgramData\PavDPI\logs\PavDPI.log
State:     C:\ProgramData\PavDPI\state.txt

These logs remain on your computer and are never uploaded.

### Security and Privacy

PavDPI is not a VPN. It does not hide your IP address or add encryption to
your internet traffic. Administrator permission is required for its Windows
service and network components; it does not bypass UAC. PavDPI does not show
ads, collect telemetry, or silently download executable files.

### Building from Source

Requirements:

- 64-bit Windows 10 or Windows 11
- .NET Framework 4.x C# compiler
- PowerShell 5.1 or newer

Build:

powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1

Run all tests:

powershell -ExecutionPolicy Bypass -File .\tests\run-all.ps1

The compiled application is created at release\PavDPI.exe.

Project: https://github.com/poroksima
