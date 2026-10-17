using System.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ====================================================
// 0. تسجيل الدخول الموحد لكل الشاشات
// ====================================================
app.MapPost("/api/auth/login", (LoginRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.Ok(new { success = false, message = "يرجى إدخال اسم المستخدم وكلمة المرور." });

    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        string sql = @"
            SELECT u.UserID, u.FullName, u.PasswordHash, r.RoleName, 
                   COALESCE(u.DoctorScope, 'كشوفات وفحوصات') AS DoctorScope,
                   COALESCE(u.AllowedScreens, 'all') AS AllowedScreens
            FROM Users u
            INNER JOIN Roles r ON u.RoleID = r.RoleID
            WHERE u.Username = @Username AND (u.IsActive = 1 OR u.IsActive IS NULL);";

        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@Username", req.Username.Trim());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Results.Ok(new { success = false, message = "اسم المستخدم غير مسجل أو غير مفعل." });

        string dbPass = reader["PasswordHash"]?.ToString() ?? "";
        string roleName = reader["RoleName"]?.ToString() ?? "Reception";
        string fullName = reader["FullName"]?.ToString() ?? "";
        string allowedScreens = reader["AllowedScreens"]?.ToString() ?? "all";
        long userId = Convert.ToInt64(reader["UserID"]);

        if (dbPass != req.Password.Trim()) return Results.Ok(new { success = false, message = "كلمة المرور غير صحيحة." });

        string targetUrl = "/reception.html";
        string roleLow = roleName.ToLower();

        if (roleLow is "doctor" or "consultant" or "resident" or "specialist") targetUrl = "/doctor.html";
        else if (roleLow == "callcenter") targetUrl = "/callcenter.html";
        else if (roleLow == "admin") targetUrl = "/admin.html";
        else if (roleLow == "dilation") targetUrl = "/dilation.html";

        return Results.Ok(new { success = true, userId, fullName, roleName, allowedScreens, redirectUrl = targetUrl });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = "خطأ اتصال بالسحاب: " + ex.Message });
    }
});

// ====================================================
// 1. لوحة الإدارة: شركات التأمين (CRUD كامل)
// ====================================================
app.MapGet("/api/admin/insurance", () =>
{
    var list = new List<object>();
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = "SELECT Id, NameAr, NameEn, DefaultCopay, COALESCE(Notes, '') AS Notes FROM MasterInsurance WHERE IsActive = 1 ORDER BY Id DESC;";
        using var cmd = new NpgsqlCommand(sql, con);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new
            {
                id = r["Id"],
                nameAr = r["NameAr"].ToString(),
                nameEn = r["NameEn"]?.ToString() ?? "",
                defaultCopay = r["DefaultCopay"]?.ToString() ?? "10%",
                notes = r["Notes"].ToString()
            });
        }
    }
    catch { }
    return Results.Ok(list);
});

app.MapPost("/api/admin/insurance", (InsuranceDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = "INSERT INTO MasterInsurance (NameAr, NameEn, DefaultCopay, Notes, IsActive) VALUES (@NameAr, @NameEn, @DefaultCopay, @Notes, 1);";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@NameAr", dto.NameAr);
        cmd.Parameters.AddWithValue("@NameEn", string.IsNullOrWhiteSpace(dto.NameEn) ? dto.NameAr : dto.NameEn);
        cmd.Parameters.AddWithValue("@DefaultCopay", string.IsNullOrWhiteSpace(dto.DefaultCopay) ? "10%" : dto.DefaultCopay);
        cmd.Parameters.AddWithValue("@Notes", (object?)dto.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حفظ شركة التأمين بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapPut("/api/admin/insurance/{id:int}", (int id, InsuranceDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = "UPDATE MasterInsurance SET NameAr = @NameAr, NameEn = @NameEn, DefaultCopay = @DefaultCopay, Notes = @Notes WHERE Id = @ID;";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.Parameters.AddWithValue("@NameAr", dto.NameAr);
        cmd.Parameters.AddWithValue("@NameEn", string.IsNullOrWhiteSpace(dto.NameEn) ? dto.NameAr : dto.NameEn);
        cmd.Parameters.AddWithValue("@DefaultCopay", string.IsNullOrWhiteSpace(dto.DefaultCopay) ? "10%" : dto.DefaultCopay);
        cmd.Parameters.AddWithValue("@Notes", (object?)dto.Notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم تعديل شركة التأمين بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapDelete("/api/admin/insurance/{id:int}", (int id) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = new NpgsqlCommand("DELETE FROM MasterInsurance WHERE Id = @ID;", con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حذف شركة التأمين بنجاح." });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// ====================================================
// 2. لوحة الإدارة: الفحوصات والأسعار والعمليات (CRUD كامل)
// ====================================================
app.MapGet("/api/admin/services", () =>
{
    var list = new List<object>();
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = @"
            SELECT ServiceID, Category, ServiceName, Price, 
                   COALESCE(PriceSingle, Price) AS PriceSingle, 
                   COALESCE(PriceBoth, Price * 2) AS PriceBoth, 
                   COALESCE(FollowupPrice, 0) AS FollowupPrice, 
                   COALESCE(FollowupDays, 14) AS FollowupDays, 
                   COALESCE(EyeSide, 'عام') AS EyeSide 
            FROM ClinicServices 
            WHERE IsActive = 1 
            ORDER BY Category, ServiceName;";
        using var cmd = new NpgsqlCommand(sql, con);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new
            {
                id = r["ServiceID"],
                category = r["Category"].ToString(),
                name = r["ServiceName"].ToString(),
                price = r["Price"],
                priceSingle = r["PriceSingle"],
                priceBoth = r["PriceBoth"],
                followupPrice = r["FollowupPrice"],
                followupDays = r["FollowupDays"],
                eyeSide = r["EyeSide"].ToString()
            });
        }
    }
    catch { }
    return Results.Ok(list);
});

app.MapPost("/api/admin/services", (ServiceAdminDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = @"
            INSERT INTO ClinicServices (Category, ServiceName, Price, PriceSingle, PriceBoth, FollowupPrice, FollowupDays, EyeSide, IsActive)
            VALUES (@Cat, @Name, @Price, @PriceSingle, @PriceBoth, @FollowupPrice, @FollowupDays, @EyeSide, 1);";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@Cat", dto.Category);
        cmd.Parameters.AddWithValue("@Name", dto.Name);
        cmd.Parameters.AddWithValue("@Price", dto.Price);
        cmd.Parameters.AddWithValue("@PriceSingle", (object?)dto.PriceSingle ?? dto.Price);
        cmd.Parameters.AddWithValue("@PriceBoth", (object?)dto.PriceBoth ?? (dto.Price * 2));
        cmd.Parameters.AddWithValue("@FollowupPrice", (object?)dto.FollowupPrice ?? 0);
        cmd.Parameters.AddWithValue("@FollowupDays", (object?)dto.FollowupDays ?? 14);
        cmd.Parameters.AddWithValue("@EyeSide", (object?)dto.EyeSide ?? "عام");
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حفظ الخدمة بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapPut("/api/admin/services/{id:int}", (int id, ServiceAdminDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = @"
            UPDATE ClinicServices 
            SET Category = @Cat, ServiceName = @Name, Price = @Price, 
                PriceSingle = @PriceSingle, PriceBoth = @PriceBoth, 
                FollowupPrice = @FollowupPrice, FollowupDays = @FollowupDays, EyeSide = @EyeSide
            WHERE ServiceID = @ID;";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.Parameters.AddWithValue("@Cat", dto.Category);
        cmd.Parameters.AddWithValue("@Name", dto.Name);
        cmd.Parameters.AddWithValue("@Price", dto.Price);
        cmd.Parameters.AddWithValue("@PriceSingle", (object?)dto.PriceSingle ?? dto.Price);
        cmd.Parameters.AddWithValue("@PriceBoth", (object?)dto.PriceBoth ?? (dto.Price * 2));
        cmd.Parameters.AddWithValue("@FollowupPrice", (object?)dto.FollowupPrice ?? 0);
        cmd.Parameters.AddWithValue("@FollowupDays", (object?)dto.FollowupDays ?? 14);
        cmd.Parameters.AddWithValue("@EyeSide", (object?)dto.EyeSide ?? "عام");
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم تعديل الخدمة بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapDelete("/api/admin/services/{id:int}", (int id) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = new NpgsqlCommand("DELETE FROM ClinicServices WHERE ServiceID = @ID;", con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حذف الخدمة بنجاح." });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// ====================================================
// 3. لوحة الإدارة: الأدوية والقطرات (CRUD كامل)
// ====================================================
app.MapGet("/api/admin/medications", () =>
{
    var list = new List<object>();
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = "SELECT MedicationID, TradeName, Category, DefaultDose, Instructions FROM Medications WHERE IsActive = 1 ORDER BY MedicationID DESC;";
    using var cmd = new NpgsqlCommand(sql, con);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        list.Add(new { 
            id = r["MedicationID"], 
            name = r["TradeName"].ToString(), 
            category = r["Category"]?.ToString() ?? "", 
            defaultDose = r["DefaultDose"]?.ToString() ?? "", 
            instructions = r["Instructions"]?.ToString() ?? "" 
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/admin/medications", (MedicationAdminDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = "INSERT INTO Medications (TradeName, Category, DefaultDose, Instructions, IsActive) VALUES (@Name, @Cat, @Dose, @Inst, 1);";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@Name", dto.Name);
        cmd.Parameters.AddWithValue("@Cat", (object?)dto.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Dose", (object?)dto.DefaultDose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Inst", (object?)dto.Instructions ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حفظ الدواء بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapPut("/api/admin/medications/{id:int}", (int id, MedicationAdminDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = "UPDATE Medications SET TradeName = @Name, Category = @Cat, DefaultDose = @Dose, Instructions = @Inst WHERE MedicationID = @ID;";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.Parameters.AddWithValue("@Name", dto.Name);
        cmd.Parameters.AddWithValue("@Cat", (object?)dto.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Dose", (object?)dto.DefaultDose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Inst", (object?)dto.Instructions ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم تعديل الدواء بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapDelete("/api/admin/medications/{id:int}", (int id) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = new NpgsqlCommand("DELETE FROM Medications WHERE MedicationID = @ID;", con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حذف الدواء بنجاح." });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// ====================================================
// 4. لوحة الإدارة: المستخدمين والأطباء (CRUD كامل)
// ====================================================
app.MapGet("/api/admin/users", () =>
{
    var list = new List<object>();
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = @"
        SELECT u.UserID, u.Username, u.FullName, COALESCE(r.RoleName, 'Doctor') AS RoleName, 
               COALESCE(u.DoctorScope, 'كشوفات وفحوصات') AS DoctorScope, 
               COALESCE(u.Notes, '') AS Title,
               COALESCE(u.AllowedScreens, 'all') AS AllowedScreens
        FROM Users u
        LEFT JOIN Roles r ON u.RoleID = r.RoleID
        WHERE u.IsActive = 1 OR u.IsActive IS NULL
        ORDER BY u.UserID DESC;";
    using var cmd = new NpgsqlCommand(sql, con);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        list.Add(new 
        { 
            id = r["UserID"], 
            username = r["Username"].ToString(), 
            fullName = r["FullName"].ToString(), 
            roleName = r["RoleName"].ToString(), 
            doctorScope = r["DoctorScope"].ToString(), 
            title = r["Title"].ToString(),
            allowedScreens = r["AllowedScreens"].ToString()
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/admin/users", (UserCreateDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string roleSql = "SELECT RoleID FROM Roles WHERE RoleName = @RName LIMIT 1;";
        int roleId = 1;
        using (var cmdR = new NpgsqlCommand(roleSql, con))
        {
            cmdR.Parameters.AddWithValue("@RName", dto.RoleName);
            var res = cmdR.ExecuteScalar();
            if (res != null) roleId = Convert.ToInt32(res);
        }

        string sql = @"
            INSERT INTO Users (Username, PasswordHash, FullName, RoleID, DoctorScope, Notes, AllowedScreens, IsActive)
            VALUES (@Username, @Password, @FullName, @RoleID, @Scope, @Title, @Screens, 1);";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@Username", dto.Username.Trim());
        cmd.Parameters.AddWithValue("@Password", dto.Password.Trim());
        cmd.Parameters.AddWithValue("@FullName", dto.FullName.Trim());
        cmd.Parameters.AddWithValue("@RoleID", roleId);
        cmd.Parameters.AddWithValue("@Scope", (object?)dto.DoctorScope ?? "كشوفات وفحوصات");
        cmd.Parameters.AddWithValue("@Title", (object?)dto.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Screens", string.IsNullOrWhiteSpace(dto.AllowedScreens) ? "all" : dto.AllowedScreens);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم إنشاء المستخدم بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapPut("/api/admin/users/{id:int}", (int id, UserUpdateDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string roleSql = "SELECT RoleID FROM Roles WHERE RoleName = @RName LIMIT 1;";
        int roleId = 1;
        using (var cmdR = new NpgsqlCommand(roleSql, con))
        {
            cmdR.Parameters.AddWithValue("@RName", dto.RoleName);
            var res = cmdR.ExecuteScalar();
            if (res != null) roleId = Convert.ToInt32(res);
        }

        string passUpdate = !string.IsNullOrWhiteSpace(dto.Password) ? ", PasswordHash = @Password" : "";
        string sql = $@"
            UPDATE Users 
            SET FullName = @FullName, RoleID = @RoleID, DoctorScope = @Scope, Notes = @Title, 
                AllowedScreens = @Screens {passUpdate}
            WHERE UserID = @ID;";
        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.Parameters.AddWithValue("@FullName", dto.FullName.Trim());
        cmd.Parameters.AddWithValue("@RoleID", roleId);
        cmd.Parameters.AddWithValue("@Scope", (object?)dto.DoctorScope ?? "كشوفات وفحوصات");
        cmd.Parameters.AddWithValue("@Title", (object?)dto.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Screens", string.IsNullOrWhiteSpace(dto.AllowedScreens) ? "all" : dto.AllowedScreens);
        if (!string.IsNullOrWhiteSpace(dto.Password)) cmd.Parameters.AddWithValue("@Password", dto.Password.Trim());
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم تعديل المستخدم بنجاح!" });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

app.MapDelete("/api/admin/users/{id:int}", (int id) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = new NpgsqlCommand("DELETE FROM Users WHERE UserID = @ID;", con);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.ExecuteNonQuery();
        return Results.Ok(new { success = true, message = "تم حذف المستخدم بنجاح." });
    }
    catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
});

// ====================================================
// 5. لوحة الإدارة: دمج ملفات المرضى المتكررة
// ====================================================
app.MapPost("/api/admin/merge-patients", (MergePatientsDto dto) =>
{
    if (dto.MasterPatientId <= 0 || dto.DuplicatePatientId <= 0 || dto.MasterPatientId == dto.DuplicatePatientId)
        return Results.Ok(new { success = false, message = "يرجى تحديد كود الملف الأساسي والمكرر بشكل صحيح!" });

    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var trans = con.BeginTransaction();

        using (var cmd = new NpgsqlCommand("UPDATE Appointments SET PatientID = @M WHERE PatientID = @D;", con, trans))
        {
            cmd.Parameters.AddWithValue("@M", dto.MasterPatientId);
            cmd.Parameters.AddWithValue("@D", dto.DuplicatePatientId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new NpgsqlCommand("UPDATE PatientQueue SET PatientID = @M WHERE PatientID = @D;", con, trans))
        {
            cmd.Parameters.AddWithValue("@M", dto.MasterPatientId);
            cmd.Parameters.AddWithValue("@D", dto.DuplicatePatientId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new NpgsqlCommand("UPDATE PatientAttachments SET PatientID = @M WHERE PatientID = @D;", con, trans))
        {
            cmd.Parameters.AddWithValue("@M", dto.MasterPatientId);
            cmd.Parameters.AddWithValue("@D", dto.DuplicatePatientId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new NpgsqlCommand(@"
            UPDATE Patients 
            SET IsDeleted = 1, 
                FullName = FullName || ' [مدمج ومحذوف]', 
                NationalID = NULL, 
                Notes = COALESCE(Notes, '') || ' [تم دمجه بالملف: ' || CAST(@M AS VARCHAR) || ']'
            WHERE PatientID = @D;", con, trans))
        {
            cmd.Parameters.AddWithValue("@M", dto.MasterPatientId);
            cmd.Parameters.AddWithValue("@D", dto.DuplicatePatientId);
            cmd.ExecuteNonQuery();
        }

        trans.Commit();
        return Results.Ok(new { success = true, message = $"✅ تم دمج ونقل سجلات الملف (#{dto.DuplicatePatientId}) إلى الملف الأصلي (#{dto.MasterPatientId}) بنجاح!" });
    }
    catch (Exception ex) { return Results.Ok(new { success = false, message = "خطأ أثناء الدمج: " + ex.Message }); }
});

// ====================================================
// 6. الاستقبال (المرضى - الحجز - فحص التكرار)
// ====================================================
app.MapGet("/api/reception/next-code", () =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(PatientID), 0) + 1 FROM Patients;", con);
        var nextCode = Convert.ToInt64(cmd.ExecuteScalar() ?? 1);
        return Results.Ok(new { nextCode });
    }
    catch { return Results.Ok(new { nextCode = 1 }); }
});

// البحث السريع الآمن
// 1. البحث السريع المضمون 100% مع PostgreSQL
app.MapGet("/api/reception/search", (string? q) =>
{
    var list = new List<object>();
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(list);
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string cleanQ = q.Trim();
        long.TryParse(cleanQ, out long exactId);

        string sql = @"
            SELECT PatientID, COALESCE(FullName, '') AS FullName, 
                   COALESCE(Mobile1, '') AS Mobile1, COALESCE(NationalID, '') AS NationalID, 
                   TO_CHAR(CreatedDate, 'YYYY-MM-DD') AS createdAt
            FROM Patients 
            WHERE COALESCE(IsDeleted, 0) = 0 
              AND (FullName ILIKE @Q OR FirstNameAr ILIKE @Q OR Mobile1 ILIKE @Q OR NationalID ILIKE @Q OR PatientID = @ExactId)
            ORDER BY PatientID DESC LIMIT 20;";

        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@Q", "%" + cleanQ + "%");
        cmd.Parameters.AddWithValue("@ExactId", exactId > 0 ? exactId : -1L);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                patientId = reader["PatientID"],
                fullName = reader["FullName"].ToString(),
                mobile = reader["Mobile1"]?.ToString() ?? "",
                nationalId = reader["NationalID"]?.ToString() ?? "",
                createdAt = reader["createdAt"]?.ToString() ?? ""
            });
        }
    }
    catch (Exception ex) { Console.WriteLine("Quick Search Error: " + ex.Message); }
    return Results.Ok(list);
});

// 2. البحث المتقدم الذكي (بدون أخطاء أنواع البيانات في PostgreSQL)
app.MapGet("/api/reception/search-advanced", (string? ar1, string? ar2, string? ar3, string? ar4, string? mobile, string? nationalId, string? from, string? to) =>
{
    var list = new List<object>();
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        var conditions = new List<string> { "COALESCE(IsDeleted, 0) = 0" };
        using var cmd = new NpgsqlCommand();
        cmd.Connection = con;

        if (!string.IsNullOrWhiteSpace(ar1)) { conditions.Add("FirstNameAr ILIKE @Ar1"); cmd.Parameters.AddWithValue("@Ar1", "%" + ar1.Trim() + "%"); }
        if (!string.IsNullOrWhiteSpace(ar2)) { conditions.Add("SecondNameAr ILIKE @Ar2"); cmd.Parameters.AddWithValue("@Ar2", "%" + ar2.Trim() + "%"); }
        if (!string.IsNullOrWhiteSpace(ar3)) { conditions.Add("ThirdNameAr ILIKE @Ar3"); cmd.Parameters.AddWithValue("@Ar3", "%" + ar3.Trim() + "%"); }
        if (!string.IsNullOrWhiteSpace(ar4)) { conditions.Add("FourthNameAr ILIKE @Ar4"); cmd.Parameters.AddWithValue("@Ar4", "%" + ar4.Trim() + "%"); }
        if (!string.IsNullOrWhiteSpace(mobile)) { conditions.Add("Mobile1 ILIKE @Mobile"); cmd.Parameters.AddWithValue("@Mobile", "%" + mobile.Trim() + "%"); }
        if (!string.IsNullOrWhiteSpace(nationalId)) { conditions.Add("NationalID ILIKE @NatId"); cmd.Parameters.AddWithValue("@NatId", "%" + nationalId.Trim() + "%"); }
        
        if (DateTime.TryParse(from, out var fd)) { conditions.Add("CAST(CreatedDate AS DATE) >= @FromDate"); cmd.Parameters.AddWithValue("@FromDate", fd.Date); }
        if (DateTime.TryParse(to, out var td)) { conditions.Add("CAST(CreatedDate AS DATE) <= @ToDate"); cmd.Parameters.AddWithValue("@ToDate", td.Date); }

        string whereClause = string.Join(" AND ", conditions);
        cmd.CommandText = $@"
            SELECT PatientID, COALESCE(FullName, '') AS FullName, 
                   COALESCE(Mobile1, '') AS Mobile1, COALESCE(NationalID, '') AS NationalID, 
                   TO_CHAR(CreatedDate, 'YYYY-MM-DD') AS createdAt
            FROM Patients 
            WHERE {whereClause}
            ORDER BY PatientID DESC LIMIT 30;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                patientId = reader["PatientID"],
                fullName = reader["FullName"].ToString(),
                mobile = reader["Mobile1"]?.ToString() ?? "",
                nationalId = reader["NationalID"]?.ToString() ?? "",
                createdAt = reader["createdAt"]?.ToString() ?? ""
            });
        }
    }
    catch (Exception ex) { Console.WriteLine("Adv Search Error: " + ex.Message); }
    return Results.Ok(list);
});
app.MapGet("/api/reception/patient/{id:long}", (long id) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        using var cmdPatient = new NpgsqlCommand("SELECT * FROM Patients WHERE PatientID = @ID AND IsDeleted = 0", con);
        cmdPatient.Parameters.AddWithValue("@ID", id);
        using var reader = cmdPatient.ExecuteReader();
        if (!reader.Read()) return Results.NotFound(new { message = "المريض غير موجود" });

        var patient = new
        {
            patientId = reader["PatientID"],
            ar1 = reader["FirstNameAr"]?.ToString() ?? "",
            ar2 = reader["SecondNameAr"]?.ToString() ?? "",
            ar3 = reader["ThirdNameAr"]?.ToString() ?? "",
            ar4 = reader["FourthNameAr"]?.ToString() ?? "",
            fullName = reader["FullName"]?.ToString() ?? "",
            nationalId = reader["NationalID"]?.ToString() ?? "",
            birthDate = reader["BirthDate"] != DBNull.Value ? Convert.ToDateTime(reader["BirthDate"]).ToString("yyyy-MM-dd") : "",
            age = reader["Age"] != DBNull.Value ? reader["Age"] : 0,
            gender = reader["Gender"]?.ToString() ?? "ذكر",
            mobile = reader["Mobile1"]?.ToString() ?? "",
            gov = reader["City"]?.ToString() ?? "",
            district = reader["District"]?.ToString() ?? "",
            address = reader["DetailedAddress"]?.ToString() ?? "",
            photoData = reader["PhotoData"]?.ToString() ?? "",
            insuranceType = reader["InsuranceType"]?.ToString() ?? "نقدي",
            insuranceCompany = reader["InsuranceCompany"]?.ToString() ?? "",
            insuranceCardNo = reader["InsuranceCardNo"]?.ToString() ?? "",
            insuranceMemberType = reader["InsuranceMemberType"]?.ToString() ?? "عضو أصيل",
            insurancePrimaryHolder = reader["InsurancePrimaryHolder"]?.ToString() ?? "",
            insuranceCopay = reader["InsuranceCopay"]?.ToString() ?? "",
            notes = reader["Notes"]?.ToString() ?? ""
        };
        reader.Close();

        string histSql = @"
            SELECT a.AppointmentID AS appointmentId, a.ServiceType AS serviceType, 
                   COALESCE(a.EyeSide, '') AS eyeSide,
                   TO_CHAR(a.AppointmentDate, 'YYYY-MM-DD') AS visitDate, 
                   TO_CHAR(a.AppointmentDate, 'HH24:MI:SS') AS visitTime, 
                   COALESCE(u.FullName, 'عيادة عامة') AS doctorName, 
                   CASE 
                       WHEN a.ServiceType LIKE '%استشارة%' OR a.ServiceType LIKE '%تقرير%' THEN '-'
                       ELSE 'INV-' || CAST(a.AppointmentID AS VARCHAR)
                   END AS invoiceNo,
                   COALESCE(a.Source, 'مدير النظام') AS createdBy,
                   COALESCE(a.Notes, '') AS NotesField
            FROM Appointments a 
            LEFT JOIN Users u ON a.DoctorID = u.UserID 
            WHERE a.PatientID = @ID ORDER BY a.AppointmentDate ASC;";

        using var cmdHist = new NpgsqlCommand(histSql, con);
        cmdHist.Parameters.AddWithValue("@ID", id);
        using var rHist = cmdHist.ExecuteReader();
        var history = new List<object>();
        while (rHist.Read())
        {
            string notesRaw = rHist["NotesField"].ToString();
            string finalDocName = rHist["doctorName"].ToString();
            if (finalDocName == "عيادة عامة" && notesRaw.Contains("[Doc:")) {
                int start = notesRaw.IndexOf("[Doc:") + 5; int end = notesRaw.IndexOf("]", start);
                if (end > start) finalDocName = notesRaw.Substring(start, end - start);
            }
            string srv = rHist["serviceType"].ToString();
            string inv = (srv.Contains("استشارة") || srv.Contains("تقرير")) ? "-" : "INV-" + rHist["appointmentId"];

            history.Add(new { appointmentId = rHist["appointmentId"], serviceType = srv, eyeSide = rHist["eyeSide"].ToString(), visitDate = rHist["visitDate"].ToString(), visitTime = rHist["visitTime"].ToString(), doctorName = finalDocName, invoiceNo = inv, createdBy = rHist["createdBy"].ToString() });
        }

        return Results.Ok(new { patient, history });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = "خطأ: " + ex.Message });
    }
});

app.MapPost("/api/reception/patients", (PatientFullDto dto) =>
{
    try
    {
        string fullCombinedName = $"{dto.Ar1?.Trim()} {dto.Ar2?.Trim()} {dto.Ar3?.Trim()} {dto.Ar4?.Trim()}".Trim();
        if (string.IsNullOrWhiteSpace(fullCombinedName))
            return Results.BadRequest(new { success = false, message = "يرجى إدخال اسم المريض!" });

        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        object birthDateDb = DateTime.TryParse(dto.BirthDate, out var bDate) ? bDate : DBNull.Value;
        object ageDb = dto.Age.HasValue && dto.Age.Value > 0 ? dto.Age.Value : DBNull.Value;

        if (dto.PatientId > 0)
        {
            string updateSql = @"
                UPDATE Patients 
                SET FirstNameAr = @Ar1, SecondNameAr = @Ar2, ThirdNameAr = @Ar3, FourthNameAr = @Ar4,
                    FullName = @FullName, NationalID = @NationalID, BirthDate = @BirthDate, Age = @Age,
                    Gender = @Gender, City = @Gov, District = @District, DetailedAddress = @AddressDetail,
                    Mobile1 = @Mobile, PhotoData = COALESCE(@Photo, PhotoData),
                    InsuranceType = @InsType, InsuranceCompany = @InsCompany,
                    InsuranceCardNo = @InsCardNo, InsuranceMemberType = @InsMemberType,
                    InsurancePrimaryHolder = @InsHolder, InsuranceCopay = @InsCopay, Notes = @Notes
                WHERE PatientID = @ID;";

            using var cmdUp = new NpgsqlCommand(updateSql, con);
            cmdUp.Parameters.AddWithValue("@ID", dto.PatientId);
            cmdUp.Parameters.AddWithValue("@Ar1", dto.Ar1?.Trim() ?? "");
            cmdUp.Parameters.AddWithValue("@Ar2", dto.Ar2?.Trim() ?? "");
            cmdUp.Parameters.AddWithValue("@Ar3", dto.Ar3?.Trim() ?? "");
            cmdUp.Parameters.AddWithValue("@Ar4", dto.Ar4?.Trim() ?? "");
            cmdUp.Parameters.AddWithValue("@FullName", fullCombinedName);
            cmdUp.Parameters.AddWithValue("@NationalID", string.IsNullOrWhiteSpace(dto.NationalId) ? (object)DBNull.Value : dto.NationalId.Trim());
            cmdUp.Parameters.AddWithValue("@BirthDate", birthDateDb);
            cmdUp.Parameters.AddWithValue("@Age", ageDb);
            cmdUp.Parameters.AddWithValue("@Gender", string.IsNullOrWhiteSpace(dto.Gender) ? "ذكر" : dto.Gender);
            cmdUp.Parameters.AddWithValue("@Gov", string.IsNullOrWhiteSpace(dto.Gov) ? "القاهرة" : dto.Gov);
            cmdUp.Parameters.AddWithValue("@District", string.IsNullOrWhiteSpace(dto.District) ? "عين شمس" : dto.District);
            cmdUp.Parameters.AddWithValue("@AddressDetail", string.IsNullOrWhiteSpace(dto.Address) ? (object)DBNull.Value : dto.Address.Trim());
            cmdUp.Parameters.AddWithValue("@Mobile", string.IsNullOrWhiteSpace(dto.Mobile) ? (object)DBNull.Value : dto.Mobile.Trim());
            cmdUp.Parameters.AddWithValue("@Photo", string.IsNullOrWhiteSpace(dto.PhotoData) ? (object)DBNull.Value : dto.PhotoData);
            cmdUp.Parameters.AddWithValue("@InsType", dto.InsuranceType ?? "نقدي");
            cmdUp.Parameters.AddWithValue("@InsCompany", (object?)dto.InsuranceCompany ?? DBNull.Value);
            cmdUp.Parameters.AddWithValue("@InsCardNo", (object?)dto.InsuranceCardNo ?? DBNull.Value);
            cmdUp.Parameters.AddWithValue("@InsMemberType", (object?)dto.InsuranceMemberType ?? DBNull.Value);
            cmdUp.Parameters.AddWithValue("@InsHolder", (object?)dto.InsurancePrimaryHolder ?? DBNull.Value);
            cmdUp.Parameters.AddWithValue("@InsCopay", (object?)dto.InsuranceCopay ?? DBNull.Value);
            cmdUp.Parameters.AddWithValue("@Notes", (object?)dto.Notes ?? DBNull.Value);
            cmdUp.ExecuteNonQuery();

            return Results.Ok(new { success = true, patientId = dto.PatientId, message = "تم تعديل ملف المريض بنجاح!" });
        }
        else
        {
            string insertSql = @"
                INSERT INTO Patients (
                    FileNumber, FirstNameAr, SecondNameAr, ThirdNameAr, FourthNameAr, FullName,
                    NationalID, BirthDate, Age, Gender, City, District, DetailedAddress, Mobile1, PhotoData,
                    InsuranceType, InsuranceCompany, InsuranceCardNo, InsuranceMemberType,
                    InsurancePrimaryHolder, InsuranceCopay, Notes, CreatedBy, CreatedDate
                )
                VALUES (
                    @FileNumber, @Ar1, @Ar2, @Ar3, @Ar4, @FullName,
                    @NationalID, @BirthDate, @Age, @Gender, @Gov, @District, @AddressDetail, @Mobile, @Photo,
                    @InsType, @InsCompany, @InsCardNo, @InsMemberType,
                    @InsHolder, @InsCopay, @Notes, 'استقبال ويب', CURRENT_TIMESTAMP
                )
                RETURNING PatientID;";

            using var cmd = new NpgsqlCommand(insertSql, con);
            cmd.Parameters.AddWithValue("@FileNumber", "F-" + DateTime.Now.ToString("yyMMddHHmmss"));
            cmd.Parameters.AddWithValue("@Ar1", dto.Ar1?.Trim() ?? "");
            cmd.Parameters.AddWithValue("@Ar2", dto.Ar2?.Trim() ?? "");
            cmd.Parameters.AddWithValue("@Ar3", dto.Ar3?.Trim() ?? "");
            cmd.Parameters.AddWithValue("@Ar4", dto.Ar4?.Trim() ?? "");
            cmd.Parameters.AddWithValue("@FullName", fullCombinedName);
            cmd.Parameters.AddWithValue("@NationalID", string.IsNullOrWhiteSpace(dto.NationalId) ? (object)DBNull.Value : dto.NationalId.Trim());
            cmd.Parameters.AddWithValue("@BirthDate", birthDateDb);
            cmd.Parameters.AddWithValue("@Age", ageDb);
            cmd.Parameters.AddWithValue("@Gender", string.IsNullOrWhiteSpace(dto.Gender) ? "ذكر" : dto.Gender);
            cmd.Parameters.AddWithValue("@Gov", string.IsNullOrWhiteSpace(dto.Gov) ? "القاهرة" : dto.Gov);
            cmd.Parameters.AddWithValue("@District", string.IsNullOrWhiteSpace(dto.District) ? "عين شمس" : dto.District);
            cmd.Parameters.AddWithValue("@AddressDetail", string.IsNullOrWhiteSpace(dto.Address) ? (object)DBNull.Value : dto.Address.Trim());
            cmd.Parameters.AddWithValue("@Mobile", string.IsNullOrWhiteSpace(dto.Mobile) ? (object)DBNull.Value : dto.Mobile.Trim());
            cmd.Parameters.AddWithValue("@Photo", string.IsNullOrWhiteSpace(dto.PhotoData) ? (object)DBNull.Value : dto.PhotoData);
            cmd.Parameters.AddWithValue("@InsType", dto.InsuranceType ?? "نقدي");
            cmd.Parameters.AddWithValue("@InsCompany", (object?)dto.InsuranceCompany ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@InsCardNo", (object?)dto.InsuranceCardNo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@InsMemberType", (object?)dto.InsuranceMemberType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@InsHolder", (object?)dto.InsurancePrimaryHolder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@InsCopay", (object?)dto.InsuranceCopay ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", (object?)dto.Notes ?? DBNull.Value);

            var newId = Convert.ToInt64(cmd.ExecuteScalar());
            return Results.Ok(new { success = true, patientId = newId, message = "تم حفظ ملف المريض بنجاح!" });
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = "خطأ الحفظ: " + ex.Message });
    }
});

// فحص التكرار (Duplicate Prevention)
app.MapPost("/api/reception/check-duplicate", (CheckDuplicateDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        if (!string.IsNullOrWhiteSpace(dto.NationalId) && dto.NationalId.Length == 14)
        {
            string sqlNat = @"
                SELECT PatientID, FullName 
                FROM Patients 
                WHERE NationalID = @NatID AND IsDeleted = 0 AND (@CurrentID = 0 OR PatientID != @CurrentID) LIMIT 1;";

            using var cmd = new NpgsqlCommand(sqlNat, con);
            cmd.Parameters.AddWithValue("@NatID", dto.NationalId.Trim());
            cmd.Parameters.AddWithValue("@CurrentID", dto.CurrentPatientId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                return Results.Ok(new
                {
                    isDuplicate = true,
                    level = "EXACT_NATIONAL_ID",
                    existingId = r["PatientID"],
                    existingName = r["FullName"].ToString()
                });
            }
            r.Close();
        }

        string fullCombinedName = $"{dto.Ar1?.Trim()} {dto.Ar2?.Trim()} {dto.Ar3?.Trim()} {dto.Ar4?.Trim()}".Trim();
        if (!string.IsNullOrWhiteSpace(fullCombinedName))
        {
            string sqlName = @"
                SELECT PatientID, FullName 
                FROM Patients 
                WHERE (FullName = @FullName OR (Mobile1 = @Mobile AND Mobile1 != '' AND FirstNameAr = @Ar1))
                  AND IsDeleted = 0 AND (@CurrentID = 0 OR PatientID != @CurrentID) LIMIT 1;";

            using var cmdName = new NpgsqlCommand(sqlName, con);
            cmdName.Parameters.AddWithValue("@FullName", fullCombinedName);
            cmdName.Parameters.AddWithValue("@Ar1", dto.Ar1?.Trim() ?? "");
            cmdName.Parameters.AddWithValue("@Mobile", string.IsNullOrWhiteSpace(dto.Mobile) ? "" : dto.Mobile.Trim());
            cmdName.Parameters.AddWithValue("@CurrentID", dto.CurrentPatientId);
            using var r2 = cmdName.ExecuteReader();
            if (r2.Read())
            {
                return Results.Ok(new
                {
                    isDuplicate = true,
                    level = "MATCH_NAME_PHONE",
                    existingId = r2["PatientID"],
                    existingName = r2["FullName"].ToString()
                });
            }
        }

        return Results.Ok(new { isDuplicate = false });
    }
    catch { return Results.Ok(new { isDuplicate = false }); }
});

app.MapPost("/api/reception/book-service", (BookServiceDto dto) =>
{
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        string sourceTxt = string.IsNullOrWhiteSpace(dto.UserName) || dto.UserName == "undefined" ? "مدير النظام" : dto.UserName;
        string safeDocName = string.IsNullOrWhiteSpace(dto.DoctorName) ? "" : $"[Doc:{dto.DoctorName}]";

        using (var cmdApp = new NpgsqlCommand("INSERT INTO Appointments (PatientID, DoctorID, ServiceType, EyeSide, AppointmentDate, Status, Source, Notes) VALUES (@PatientID, NULL, @ServiceType, @EyeSide, CURRENT_TIMESTAMP, 'Confirmed', @Source, @Notes);", con)) {
            cmdApp.Parameters.AddWithValue("@PatientID", dto.PatientId);
            cmdApp.Parameters.AddWithValue("@ServiceType", dto.ServiceType ?? "");
            cmdApp.Parameters.AddWithValue("@EyeSide", (object?)dto.EyeSide ?? "عام");
            cmdApp.Parameters.AddWithValue("@Source", sourceTxt);
            cmdApp.Parameters.AddWithValue("@Notes", safeDocName);
            cmdApp.ExecuteNonQuery();
        }

        string queueSql = @"
            DO $$
            DECLARE next_q INT;
            BEGIN
                SELECT COALESCE(MAX(QueueNumber), 0) + 1 INTO next_q 
                FROM PatientQueue 
                WHERE CAST(ArrivedTime AS DATE) = CURRENT_DATE AND Department = @Department;

                INSERT INTO PatientQueue (QueueNumber, PatientID, Department, DoctorName, Status, ArrivedTime)
                VALUES (next_q, @PatientID, @Department, @DoctorName, 'Waiting', CURRENT_TIMESTAMP);
            END $$;";

        using var cmdQ = new NpgsqlCommand(queueSql, con);
        cmdQ.Parameters.AddWithValue("@PatientID", dto.PatientId);
        cmdQ.Parameters.AddWithValue("@Department", dto.Clinic);
        cmdQ.Parameters.AddWithValue("@DoctorName", (object?)dto.DoctorName ?? DBNull.Value);
        cmdQ.ExecuteNonQuery();

        using var cmdGetQ = new NpgsqlCommand("SELECT MAX(QueueNumber) FROM PatientQueue WHERE CAST(ArrivedTime AS DATE) = CURRENT_DATE AND Department = @Department AND PatientID = @PatientID;", con);
        cmdGetQ.Parameters.AddWithValue("@Department", dto.Clinic);
        cmdGetQ.Parameters.AddWithValue("@PatientID", dto.PatientId);
        int queueNumber = Convert.ToInt32(cmdGetQ.ExecuteScalar() ?? 1);

        return Results.Ok(new { success = true, queueNumber });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = "خطأ في الحجز: " + ex.Message });
    }
});

// ====================================================
// 7. المرفقات والسكانر (جديد ومضاف)
// ====================================================
app.MapPost("/api/reception/upload-attachment", async (HttpRequest request) =>
{
    try
    {
        var form = await request.ReadFormAsync();
        var file = form.Files["file"];
        var scanBase64 = form["scanBase64"].ToString();
        var patientIdStr = form["patientId"];
        var title = form["title"].ToString();
        var createdBy = form["userName"].ToString();

        if (string.IsNullOrEmpty(patientIdStr))
            return Results.BadRequest(new { success = false, message = "كود المريض مفقود." });

        byte[] fileBytes;
        string fileName;
        string fileType;

        if (file != null && file.Length > 0)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
            fileName = string.IsNullOrWhiteSpace(title) ? file.FileName : $"{title} - {file.FileName}";
            fileType = file.ContentType ?? "application/octet-stream";
        }
        else if (!string.IsNullOrWhiteSpace(scanBase64))
        {
            string cleanBase64 = scanBase64.Contains(",") ? scanBase64.Substring(scanBase64.IndexOf(",") + 1) : scanBase64;
            fileBytes = Convert.FromBase64String(cleanBase64);
            fileName = string.IsNullOrWhiteSpace(title) ? $"Scanner_{DateTime.Now:yyyyMMdd_HHmmss}.jpg" : $"{title}_Scan.jpg";
            fileType = "image/jpeg";
        }
        else
        {
            return Results.BadRequest(new { success = false, message = "يرجى اختيار ملف أو التقاط من السكانر أولاً!" });
        }

        long patientId = Convert.ToInt64(patientIdStr);
        using var con = new NpgsqlConnection(connectionString);
        con.Open();

        string datePrefix = DateTime.Now.ToString("yyyyMMdd");
        string serialSql = "SELECT COALESCE(MAX(AttachmentID), 0) + 1 FROM PatientAttachments;";
        using var cmdNext = new NpgsqlCommand(serialSql, con);
        long nextId = Convert.ToInt64(cmdNext.ExecuteScalar() ?? 1);
        string serialNo = $"ATT-{datePrefix}-{nextId:D4}";

        string sql = @"
            INSERT INTO PatientAttachments (SerialNo, PatientID, FileName, FileType, FileData, CreatedBy, CreatedDate)
            VALUES (@SerialNo, @PatientID, @FileName, @FileType, @FileData, @CreatedBy, CURRENT_TIMESTAMP);";

        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@SerialNo", serialNo);
        cmd.Parameters.AddWithValue("@PatientID", patientId);
        cmd.Parameters.AddWithValue("@FileName", fileName);
        cmd.Parameters.AddWithValue("@FileType", fileType);
        cmd.Parameters.AddWithValue("@FileData", fileBytes);
        cmd.Parameters.AddWithValue("@CreatedBy", string.IsNullOrWhiteSpace(createdBy) ? "الاستقبال" : createdBy);
        cmd.ExecuteNonQuery();

        return Results.Ok(new { success = true, serialNo, message = $"تم حفظ المرفق بنجاح بالسيريال رقم: {serialNo}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = "خطأ حفظ المرفق: " + ex.Message });
    }
});

app.MapGet("/api/reception/attachments/{patientId:long}", (long patientId) =>
{
    var list = new List<object>();
    try
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        string sql = @"
            SELECT AttachmentID AS id, 
                   COALESCE(SerialNo, 'ATT-' || CAST(AttachmentID AS VARCHAR)) AS serialNo,
                   FileName AS fileName, FileType AS fileType, 
                   COALESCE(CreatedBy, 'الاستقبال') AS createdBy,
                   TO_CHAR(CreatedDate, 'YYYY-MM-DD HH24:MI') AS uploadDate
            FROM PatientAttachments
            WHERE PatientID = @PatientID
            ORDER BY AttachmentID DESC;";

        using var cmd = new NpgsqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@PatientID", patientId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                id = reader["id"],
                serialNo = reader["serialNo"].ToString(),
                fileName = reader["fileName"].ToString(),
                fileType = reader["fileType"]?.ToString() ?? "",
                createdBy = reader["createdBy"].ToString(),
                uploadDate = reader["uploadDate"].ToString()
            });
        }
    }
    catch { }
    return Results.Ok(list);
});

app.MapGet("/api/reception/attachment-download/{id:long}", (long id) =>
{
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = "SELECT FileName, FileType, FileData FROM PatientAttachments WHERE AttachmentID = @ID;";
    using var cmd = new NpgsqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@ID", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();

    string fileName = reader["FileName"].ToString();
    string fileType = reader["FileType"]?.ToString() ?? "application/octet-stream";
    byte[] fileData = (byte[])reader["FileData"];

    return Results.File(fileData, fileType, fileName);
});

// ====================================================
// 8. شاشة الطبيب والكيوهات
// ====================================================
app.MapGet("/api/admin/doctors", (string? scope) =>
{
    var list = new List<object>();
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = @"
        SELECT u.UserID, u.FullName, COALESCE(u.Notes, 'طبيب عيون') AS Title,
               COALESCE(r.RoleName, 'Specialist') AS RoleName,
               COALESCE(u.DoctorScope, 'كشوفات وفحوصات') AS DoctorScope
        FROM Users u
        INNER JOIN Roles r ON u.RoleID = r.RoleID
        WHERE LOWER(r.RoleName) IN ('doctor', 'consultant', 'resident', 'specialist')
          AND (u.IsActive = 1 OR u.IsActive IS NULL)
          AND (@Scope IS NULL OR @Scope = '' OR u.DoctorScope = 'كشوفات وفحوصات' OR u.DoctorScope = @Scope);";

    using var cmd = new NpgsqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@Scope", string.IsNullOrWhiteSpace(scope) ? DBNull.Value : scope.Trim());
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        list.Add(new { id = r["UserID"], name = r["FullName"].ToString(), title = r["Title"].ToString(), role = r["RoleName"].ToString(), doctorScope = r["DoctorScope"].ToString() });
    }
    return Results.Ok(list);
});

app.MapGet("/api/doctor/queue", (string? dept, string? doctorName, string? userRole) =>
{
    using var con = new NpgsqlConnection(connectionString);
    con.Open();

    string sql = @"
        SELECT q.QueueID AS queueId, q.QueueNumber AS queueNumber, 
               p.PatientID AS patientId, COALESCE(p.FullName, 'بدون اسم') AS fullName, 
               q.Department AS department, q.Status AS status,
               COALESCE(q.DoctorName, 'عيادة عامة') AS doctorName,
               COALESCE(q.DilationType, '') AS dilationType,
               COALESCE(q.DilationEye, '') AS dilationEye,
               COALESCE(q.NeedPostRefraction, 0) AS needPostRefraction,
               COALESCE(a.ServiceType, '') AS serviceType,
               COALESCE(a.EyeSide, 'العين المقررة') AS eyeSide
        FROM PatientQueue q
        INNER JOIN Patients p ON q.PatientID = p.PatientID
        LEFT JOIN (
            SELECT PatientID, ServiceType, EyeSide
            FROM Appointments
            WHERE CAST(AppointmentDate AS DATE) = CURRENT_DATE
        ) a ON p.PatientID = a.PatientID
        WHERE CAST(q.ArrivedTime AS DATE) = CURRENT_DATE
          AND q.Status != 'Done'
          AND (@Dept IS NULL OR @Dept = '' OR q.Department = @Dept)
        ORDER BY q.QueueNumber ASC;";

    using var cmd = new NpgsqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@Dept", string.IsNullOrWhiteSpace(dept) ? DBNull.Value : dept.Trim());

    using var reader = cmd.ExecuteReader();
    var list = new List<object>();
    while (reader.Read())
    {
        list.Add(new
        {
            queueId = reader["queueId"],
            queueNumber = reader["queueNumber"],
            patientId = reader["patientId"],
            fullName = reader["fullName"].ToString(),
            department = reader["department"].ToString(),
            doctorName = reader["doctorName"].ToString(),
            status = reader["status"].ToString(),
            dilationType = reader["dilationType"].ToString(),
            dilationEye = reader["dilationEye"].ToString(),
            needPostRefraction = Convert.ToInt32(reader["needPostRefraction"]),
            serviceType = reader["serviceType"].ToString(),
            eyeSide = reader["eyeSide"].ToString()
        });
    }
    return Results.Ok(list);
});

app.MapPost("/api/doctor/finish-visit", (DoctorVisitDto dto) =>
{
    using var con = new NpgsqlConnection(connectionString);
    con.Open();

    using (var cmdQ = new NpgsqlCommand("UPDATE PatientQueue SET Status = 'Done' WHERE QueueID = @QID;", con))
    {
        cmdQ.Parameters.AddWithValue("@QID", dto.QueueId);
        cmdQ.ExecuteNonQuery();
    }

    using (var cmdApp = new NpgsqlCommand(@"
        UPDATE Appointments SET Notes = COALESCE(Notes, '') || @Notes, Status = 'Completed'
        WHERE AppointmentID = (SELECT AppointmentID FROM Appointments WHERE PatientID = @PID AND CAST(AppointmentDate AS DATE) = CURRENT_DATE ORDER BY AppointmentDate DESC LIMIT 1);", con))
    {
        string fullDetails = $" | تشخيص: {dto.Diagnosis} | فحص: {dto.Findings} | نظارة: {dto.GlassesOd} / {dto.GlassesOs} | روشتة: {dto.Prescription}";
        cmdApp.Parameters.AddWithValue("@Notes", fullDetails);
        cmdApp.Parameters.AddWithValue("@PID", dto.PatientId);
        cmdApp.ExecuteNonQuery();
    }

    return Results.Ok(new { success = true });
});

app.MapPost("/api/doctor/request-dilation", (DilationOrderDto dto) =>
{
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = @"
        UPDATE PatientQueue 
        SET Status = 'Dilating',
            DilationType = @DType,
            DilationEye = @DEye,
            NeedPostRefraction = @NeedRefract
        WHERE QueueID = @QID;";

    using var cmd = new NpgsqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@QID", dto.QueueId);
    cmd.Parameters.AddWithValue("@DType", dto.DilationType);
    cmd.Parameters.AddWithValue("@DEye", dto.TargetEye);
    cmd.Parameters.AddWithValue("@NeedRefract", dto.NeedPostRefraction ? 1 : 0);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { success = true });
});

// ====================================================
// 9. غرفة القطرات والتوسيع (جديد ومضاف)
// ====================================================
app.MapGet("/api/dilation/queue", () =>
{
    var list = new List<object>();
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = @"
        SELECT q.QueueID AS id, q.QueueNumber AS queueNumber, p.PatientID AS patientId, p.FullName AS fullName, 
               q.Department AS department, q.Status AS status, 
               COALESCE(q.DilationType, 'Mydriacyl 1%') AS dilationType, 
               COALESCE(q.DilationEye, 'العينين (OU)') AS dilationEye, 
               COALESCE(q.NeedPostRefraction, 0) AS needPostRefraction 
        FROM PatientQueue q 
        INNER JOIN Patients p ON q.PatientID = p.PatientID 
        WHERE CAST(q.ArrivedTime AS DATE) = CURRENT_DATE 
          AND q.Status IN ('Waiting', 'Dilating', 'DilationReady') 
        ORDER BY q.QueueNumber ASC;";
    using var cmd = new NpgsqlCommand(sql, con);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        list.Add(new { queueId = r["id"], queueNumber = r["queueNumber"], patientId = r["patientId"], fullName = r["fullName"].ToString(), status = r["status"].ToString(), dilationType = r["dilationType"].ToString(), dilationEye = r["dilationEye"].ToString(), needPostRefraction = Convert.ToInt32(r["needPostRefraction"]) });
    }
    return Results.Ok(list);
});

app.MapPost("/api/dilation/apply-drop", (DilationActionDto dto) =>
{
    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = "UPDATE PatientQueue SET Status = @Status WHERE QueueID = @QID;";
    using var cmd = new NpgsqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@Status", dto.Status);
    cmd.Parameters.AddWithValue("@QID", dto.QueueId);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

// ====================================================
// 10. الكول سنتر والمشرف
// ====================================================
app.MapPost("/api/supervisor/save-schedule", (SupervisorScheduleDto dto) =>
{
    try
    {
        DateTime startDate = DateTime.Parse(dto.StartDate);
        DateTime endDate = DateTime.Parse(dto.EndDate);
        TimeSpan sTime = TimeSpan.Parse(dto.StartTime);
        TimeSpan eTime = TimeSpan.Parse(dto.EndTime);

        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var trans = con.BeginTransaction();

        for (DateTime date = startDate; date <= endDate; date = date.AddDays(1))
        {
            if (dto.DaysOfWeek.Contains((int)date.DayOfWeek))
            {
                using (var cmdDel = new NpgsqlCommand("DELETE FROM DoctorSchedules WHERE DoctorName = @Doc AND ScheduleDate = @Date;", con, trans))
                {
                    cmdDel.Parameters.AddWithValue("@Doc", dto.DoctorName);
                    cmdDel.Parameters.AddWithValue("@Date", date);
                    cmdDel.ExecuteNonQuery();
                }

                string insSql = @"
                    INSERT INTO DoctorSchedules (Department, Specialty, DoctorName, ScheduleDate, StartTime, EndTime, MaxNew, MaxFollowup, MaxScans)
                    VALUES (@Dept, @Spec, @Doc, @Date, @STime, @ETime, @MNew, @MFol, @MScan);";
                using (var cmdIns = new NpgsqlCommand(insSql, con, trans))
                {
                    cmdIns.Parameters.AddWithValue("@Dept", dto.Department);
                    cmdIns.Parameters.AddWithValue("@Spec", dto.Specialty);
                    cmdIns.Parameters.AddWithValue("@Doc", dto.DoctorName);
                    cmdIns.Parameters.AddWithValue("@Date", date);
                    cmdIns.Parameters.AddWithValue("@STime", sTime);
                    cmdIns.Parameters.AddWithValue("@ETime", eTime);
                    cmdIns.Parameters.AddWithValue("@MNew", dto.MaxNewConsultations);
                    cmdIns.Parameters.AddWithValue("@MFol", dto.MaxFollowups);
                    cmdIns.Parameters.AddWithValue("@MScan", dto.MaxScansOrLaser);
                    cmdIns.ExecuteNonQuery();
                }
            }
        }
        trans.Commit();
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/callcenter/existing-bookings", (string q) =>
{
    var list = new List<object>();
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(list);

    using var con = new NpgsqlConnection(connectionString);
    con.Open();
    string sql = @"
        SELECT a.AppointmentDate, a.ServiceType, a.Status, p.FullName, p.Mobile1
        FROM Appointments a
        INNER JOIN Patients p ON a.PatientID = p.PatientID
        WHERE CAST(p.PatientID AS VARCHAR) = @Q OR p.Mobile1 = @Q
        ORDER BY a.AppointmentDate DESC;";
    using var cmd = new NpgsqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@Q", q.Trim());
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        list.Add(new
        {
            patientName = r["FullName"].ToString(),
            mobile = r["Mobile1"].ToString(),
            appointmentDate = Convert.ToDateTime(r["AppointmentDate"]).ToString("yyyy-MM-dd HH:mm"),
            serviceType = r["ServiceType"].ToString(),
            status = r["Status"].ToString()
        });
    }
    return Results.Ok(list);
});

app.MapGet("/api/callcenter/slots", (string doctorName, string date, string? dept) =>
{
    var parsedDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date);
    
    using var con = new NpgsqlConnection(connectionString);
    con.Open();

    string schSql = "SELECT StartTime, EndTime, (MaxNew + MaxFollowup + MaxScans) AS TotalCapacity FROM DoctorSchedules WHERE DoctorName = @Doc AND ScheduleDate = @Date LIMIT 1;";
    using var cmdSch = new NpgsqlCommand(schSql, con);
    cmdSch.Parameters.AddWithValue("@Doc", doctorName ?? "");
    cmdSch.Parameters.AddWithValue("@Date", parsedDate);
    
    using var rSch = cmdSch.ExecuteReader();
    if (!rSch.Read()) 
    {
        return Results.Ok(new { slots = new List<object>(), currentBooked = 0, maxLimit = 0, isLimitReached = true });
    }

    TimeSpan start = (TimeSpan)rSch["StartTime"];
    TimeSpan end = (TimeSpan)rSch["EndTime"];
    int capacity = Convert.ToInt32(rSch["TotalCapacity"]);
    rSch.Close();

    string appSql = @"
        SELECT TO_CHAR(AppointmentDate, 'HH24:MI') AS SlotTime,
               COALESCE(p.FullName, a.Notes) AS PatientName,
               COALESCE(p.Mobile1, '') AS Mobile, a.Status
        FROM Appointments a
        LEFT JOIN Patients p ON a.PatientID = p.PatientID
        WHERE CAST(a.AppointmentDate AS DATE) = @Date
          AND (a.ServiceType LIKE '%' || @DocName || '%' OR @DocName = '');";

    using var cmdApp = new NpgsqlCommand(appSql, con);
    cmdApp.Parameters.AddWithValue("@Date", parsedDate);
    cmdApp.Parameters.AddWithValue("@DocName", doctorName ?? "");

    var bookedDict = new Dictionary<string, object>();
    using var r = cmdApp.ExecuteReader();
    while (r.Read())
    {
        var slot = r["SlotTime"].ToString() ?? "";
        bookedDict[slot] = new
        {
            slot,
            patientName = r["PatientName"].ToString(),
            mobile = r["Mobile"].ToString(),
            status = r["Status"].ToString()
        };
    }

    int currentBookedCount = bookedDict.Count;
    bool isLimitReached = currentBookedCount >= capacity;

    var allSlots = new List<object>();
    for (var ts = start; ts < end; ts = ts.Add(TimeSpan.FromMinutes(20)))
    {
        string slotStr = ts.ToString(@"hh\:mm");
        bool isBooked = bookedDict.ContainsKey(slotStr);

        allSlots.Add(new
        {
            time = slotStr,
            isBooked,
            bookingDetails = isBooked ? bookedDict[slotStr] : null
        });
    }

    return Results.Ok(new { slots = allSlots, currentBooked = currentBookedCount, maxLimit = capacity, isLimitReached });
});

app.Run();

// ====================================================
// DTOs متطابقة 100%
// ====================================================
public record LoginRequest(string Username, string Password);
public record PatientFullDto(long PatientId, string? Ar1, string? Ar2, string? Ar3, string? Ar4, string? NationalId, string? BirthDate, int? Age, string? Gender, string? Gov, string? District, string? Address, string? Mobile, string? PhotoData, string? InsuranceType, string? InsuranceCompany, string? InsuranceCardNo, string? InsuranceMemberType, string? InsurancePrimaryHolder, string? InsuranceCopay, string? Notes);
public record BookServiceDto(long PatientId, string Clinic, string ServiceType, string? EyeSide, decimal Price, string? DoctorName, string? UserName);
public record DoctorVisitDto(long PatientId, int QueueId, string? Complaint, string? Diagnosis, string? Findings, string? Prescription, string? GlassesOd, string? GlassesOs, string? Investigation, string? Surgery, string? Referral);
public record ServiceAdminDto(string Category, string Name, decimal Price, decimal? PriceSingle, decimal? PriceBoth, decimal? FollowupPrice, int? FollowupDays, string? EyeSide);
public record MedicationAdminDto(string Name, string? Category, string? DefaultDose, string? Instructions);
public record UserCreateDto(string Username, string Password, string FullName, string RoleName, string? Title, string? DoctorScope, string? AllowedScreens);
public record UserUpdateDto(string FullName, string RoleName, string? Title, string? DoctorScope, string? Password, string? AllowedScreens);
public record InsuranceDto(string NameAr, string? NameEn, string? DefaultCopay, string? Notes);
public record AuditLogDto(long PatientId, string? UserName, string? UserRole, string? ActionType, string? ActionDetails);
public record BookSlotDto(long PatientId, string PatientName, string Mobile, string Clinic, string DoctorName, string Date, string TimeSlot, string? Notes, string? ScanType, string? TargetEye, bool IsInsurance, string? InsuranceCompany);
public record DilationActionDto(int QueueId, string Status);
public record DilationOrderDto(int QueueId, string DilationType, string TargetEye, bool NeedPostRefraction);
public record CheckDuplicateDto(long CurrentPatientId, string? Ar1, string? Ar2, string? Ar3, string? Ar4, string? NationalId, string? Mobile);
public record MergePatientsDto(long MasterPatientId, long DuplicatePatientId, string? AdminUser);
public record SupervisorScheduleDto(string Department, string Specialty, string DoctorName, string StartDate, string EndDate, List<int> DaysOfWeek, string StartTime, string EndTime, int MaxNewConsultations, int MaxFollowups, int MaxScansOrLaser);