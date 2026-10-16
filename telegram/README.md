# TG Contacts Manager

تطبيق ويندوز (WPF، .NET 8) بنفس تصميم IG Story Viewer Pro (واجهة عربية داكنة) لإدارة **جهات اتصال تلجرام** من حسابك:

- **عرض** كل جهات الاتصال مع بحث بالاسم أو الرقم أو اسم المستخدم.
- **إضافة** جهة اتصال برقم الهاتف (يجب أن يكون للرقم حساب تلجرام).
- **تعديل** اسم جهة الاتصال (الأول والعائلة).
- **حذف** جهة اتصال بعد التأكيد.

## التشغيل
1. ادخل إلى <https://my.telegram.org> ← **API development tools** وأنشئ تطبيقاً لتحصل على `api_id` و `api_hash`.
2. شغّل التطبيق، أدخلهما مع رقم هاتفك، ثم الكود الذي يصلك في تلجرام (وكلمة مرور التحقق بخطوتين إن وُجدت).
3. تُحفظ الجلسة في `%AppData%\TelegramContacts` فلا تحتاج لتسجيل الدخول مجدداً. «تسجيل الخروج» ينهي الجلسة ويحذفها.

## البناء
```
dotnet publish telegram/src/TelegramContacts -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
أو حمّل `TelegramContacts.exe` من نتائج workflow **telegram-contacts** في GitHub Actions.

يستخدم مكتبة [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (بروتوكول MTProto الرسمي).
