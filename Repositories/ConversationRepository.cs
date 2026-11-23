using BtoanAiNet.Data;
using BtoanAiNet.Models;
using Microsoft.EntityFrameworkCore;

namespace BtoanAiNet.Repositories
{
    public class ConversationRepository : IConversationRepository
    {
        private readonly AppDbContext _context;

        public ConversationRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<Conversation?> GetConversationByIdAsync(int id, int userId)
        {
            return await _context.Conversations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id && (userId == 0 || c.UserId == userId));
        }

        public async Task<Conversation> CreateConversationAsync(int userId, string title)
        {
            var conv = new Conversation
            {
                UserId = userId,
                Title = title,
                CreatedAt = DateTime.UtcNow
            };
            _context.Conversations.Add(conv);
            await _context.SaveChangesAsync();
            return conv;
        }

        public async Task<List<Conversation>> GetConversationsByUserIdAsync(int userId)
        {
            var list = await _context.Conversations
                .AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.Id)
                .ToListAsync();

            foreach (var conv in list)
            {
                if (!string.IsNullOrEmpty(conv.Title) && (conv.Title.StartsWith("{") || conv.Title.StartsWith("[") || conv.Title.Contains("candidates")))
                {
                    conv.Title = "Cuộc trò chuyện";
                }
            }

            return list;
        }

        public async Task UpdateConversationTitleAsync(int id, string title)
        {
            var conv = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == id);
            if (conv != null)
            {
                conv.Title = title;
                await _context.SaveChangesAsync();
            }
        }

        public async Task DeleteConversationAsync(int id)
        {
            var chats = await _context.Chats.Where(c => c.ConversationId == id).ToListAsync();
            _context.Chats.RemoveRange(chats);

            var conv = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == id);
            if (conv != null)
            {
                _context.Conversations.Remove(conv);
            }

            await _context.SaveChangesAsync();
        }

        public async Task<List<Chat>> GetChatHistoryAsync(int conversationId, int userId, int limit = 20)
        {
            var chats = await _context.Chats
                .AsNoTracking()
                .Where(c => c.ConversationId == conversationId && (userId == 0 || c.UserId == userId))
                .OrderByDescending(c => c.Id)
                .Take(limit)
                .ToListAsync();

            return chats.OrderBy(c => c.Id).ToList();
        }

        public async Task<List<Chat>> GetMessagesByConversationIdAsync(int conversationId)
        {
            return await _context.Chats
                .AsNoTracking()
                .Where(c => c.ConversationId == conversationId)
                .OrderBy(c => c.Id)
                .ToListAsync();
        }

        public async Task<Chat> AddChatAsync(Chat chat)
        {
            _context.Chats.Add(chat);
            await _context.SaveChangesAsync();
            return chat;
        }

        public async Task DeleteAllByUserIdAsync(int userId)
        {
            var chats = await _context.Chats.Where(c => c.UserId == userId).ToListAsync();
            _context.Chats.RemoveRange(chats);
            var convs = await _context.Conversations.Where(c => c.UserId == userId).ToListAsync();
            _context.Conversations.RemoveRange(convs);
            await _context.SaveChangesAsync();
        }

        public async Task<List<object>> GetAllWithChatsByUserIdAsync(int userId)
        {
            var convs = await _context.Conversations
                .AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var convIds = convs.Select(c => c.Id).ToList();
            var allChats = await _context.Chats
                .AsNoTracking()
                .Where(c => c.ConversationId.HasValue && convIds.Contains(c.ConversationId.Value))
                .OrderBy(c => c.CreatedAt)
                .ToListAsync();

            var result = new List<object>();
            foreach (var c in convs)
            {
                var messages = allChats.Where(x => x.ConversationId == c.Id).Select(m => new
                {
                    id = m.Id,
                    question = m.Question,
                    answer = m.Answer,
                    created_at = m.CreatedAt
                }).ToList();

                result.Add(new
                {
                    id = c.Id,
                    title = c.Title,
                    created_at = c.CreatedAt,
                    messages
                });
            }
            return result;
        }
    }
}
