using BtoanAiNet.Data;
using BtoanAiNet.Models;
using Microsoft.EntityFrameworkCore;

namespace BtoanAiNet.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly AppDbContext _context;

        public UserRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<User?> GetByIdAsync(int id)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
        }

        public async Task<User?> GetByUsernameAsync(string username)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
        }

        public async Task<User?> GetByGoogleIdAsync(string googleId)
        {
            return await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);
        }

        public async Task<User> AddAsync(User user)
        {
            try
            {
                _context.Users.Add(user);
                await _context.SaveChangesAsync();
                return user;
            }
            catch (Exception ex)
            {
                // Self-healing: if database table is missing new columns, auto-alter and retry
                try
                {
                    var cols = new (string col, string type)[]
                    {
                        ("bio", "TEXT NULL"),
                        ("default_model", "VARCHAR(50) NULL DEFAULT 'Btoan 2.5 Flash'"),
                        ("custom_instructions", "TEXT NULL"),
                        ("temperature", "DOUBLE NULL DEFAULT 0.7"),
                        ("long_term_memory", "INT NULL DEFAULT 1"),
                        ("rag_enabled", "INT NULL DEFAULT 1"),
                        ("theme", "VARCHAR(20) NULL DEFAULT 'dark'"),
                        ("chat_font_size", "VARCHAR(20) NULL DEFAULT '15px'"),
                        ("tts_speed", "DOUBLE NULL DEFAULT 1.0")
                    };
                    foreach (var (col, colType) in cols)
                    {
                        try { string s = string.Format("ALTER TABLE `users` ADD COLUMN `{0}` {1};", col, colType); _context.Database.ExecuteSqlRaw(s); } catch { }
                        try { string s = string.Format("ALTER TABLE [dbo].[users] ADD [{0}] {1};", col, colType.Replace("VARCHAR(50)", "NVARCHAR(50)").Replace("TEXT", "NVARCHAR(MAX)").Replace("DOUBLE", "FLOAT")); _context.Database.ExecuteSqlRaw(s); } catch { }
                    }
                    await _context.SaveChangesAsync();
                    return user;
                }
                catch
                {
                    // Self-healing attempt 2: direct insert with base columns only (ignores missing extended columns)
                    try
                    {
                        _context.Entry(user).State = EntityState.Detached;
                        string tableName = _context.Database.IsSqlServer() ? "[dbo].[users]" : (_context.Database.IsSqlite() ? "users" : "`users`");
                        string insertSql = string.Format("INSERT INTO {0} (username, password, created_at) VALUES ({{0}}, {{1}}, {{2}})", tableName);
                        await _context.Database.ExecuteSqlRawAsync(
                            insertSql,
                            user.Username ?? "", user.Password ?? "", user.CreatedAt
                        );
                        var inserted = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == user.Username);
                        return inserted ?? user;
                    }
                    catch (Exception ex2)
                    {
                        var err = ex2.InnerException?.Message ?? ex2.Message;
                        throw new Exception($"Lỗi lưu database: {err} ({ex.InnerException?.Message ?? ex.Message})", ex2);
                    }
                }
            }
        }

        public async Task UpdateAsync(User user)
        {
            try
            {
                _context.Users.Update(user);
                await _context.SaveChangesAsync();
            }
            catch
            {
                try
                {
                    var cols = new (string col, string type)[]
                    {
                        ("bio", "TEXT NULL"),
                        ("default_model", "VARCHAR(50) NULL DEFAULT 'Btoan 2.5 Flash'"),
                        ("custom_instructions", "TEXT NULL"),
                        ("temperature", "DOUBLE NULL DEFAULT 0.7"),
                        ("long_term_memory", "INT NULL DEFAULT 1"),
                        ("rag_enabled", "INT NULL DEFAULT 1"),
                        ("theme", "VARCHAR(20) NULL DEFAULT 'dark'"),
                        ("chat_font_size", "VARCHAR(20) NULL DEFAULT '15px'"),
                        ("tts_speed", "DOUBLE NULL DEFAULT 1.0")
                    };
                    foreach (var (col, colType) in cols)
                    {
                        try { string s = string.Format("ALTER TABLE `users` ADD COLUMN `{0}` {1};", col, colType); _context.Database.ExecuteSqlRaw(s); } catch { }
                        try { string s = string.Format("ALTER TABLE [dbo].[users] ADD [{0}] {1};", col, colType.Replace("VARCHAR(50)", "NVARCHAR(50)").Replace("TEXT", "NVARCHAR(MAX)").Replace("DOUBLE", "FLOAT")); _context.Database.ExecuteSqlRaw(s); } catch { }
                    }
                    await _context.SaveChangesAsync();
                }
                catch
                {
                    throw;
                }
            }
        }

        public async Task<bool> ExistsUsernameAsync(string username)
        {
            return await _context.Users.AnyAsync(u => u.Username == username);
        }
    }
}
